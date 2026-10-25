using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.MediaHandlers;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Creates a VM from a converted disk image (VMDK/QCOW2/VHDX). Handles both GPT
    /// (Gen2) and MBR (Gen1) partition schemes by booting a customization ISO that
    /// installs packages, clones MBR to GPT if necessary, and injects an SSH key.
    /// After the ISO cycle, optional Linux post-boot steps run over SSH.
    /// </summary>
    public class DiskImageVmCreationStrategy : IVmCreationStrategy
    {
        private readonly IVmLifecycleManager _lifecycleManager;
        private readonly IVmDiskManager _diskManager;
        private readonly IVmBootManager _bootManager;
        private readonly IVmNetworkManager _networkManager;
        private readonly IVmConfigManager _configManager;
        private readonly IGuestShellFactory _guestShellFactory;
        private readonly ISshKeyManager _sshKeyManager;
        private readonly IKvpHeartbeatGate _kvpHeartbeatGate;
        private readonly IGuestDiagnosticsCollector _diagnosticsCollector;
        private readonly IPostBootCustomizationService _postBootService;
        private readonly IPostBootStepPlanner _stepPlanner;
        private readonly IHostNetworkService _hostNetworkService;
        private readonly IVmPathService _pathService;
        private readonly IIsoBootCycleRunner _isoBootRunner;
        private readonly ILogger<DiskImageVmCreationStrategy> _logger;
        private const int OriginalDiskScsiControllerLocation = 1;

        /// <summary>
        /// Free-space probe seam for tests: (path) → (free bytes, drive
        /// name). Null in production — the guard uses the real
        /// <see cref="System.IO.DriveInfo"/>.
        /// </summary>
        internal Func<string, (long FreeBytes, string DriveName)>? FreeSpaceProbe
        {
            get => _freeSpaceProbe;
            set => _freeSpaceProbe = value;
        }

        private Func<string, (long FreeBytes, string DriveName)>? _freeSpaceProbe;

        public DiskImageVmCreationStrategy(
            IVmLifecycleManager lifecycleManager,
            IVmDiskManager diskManager,
            IVmBootManager bootManager,
            IVmNetworkManager networkManager,
            IVmConfigManager configManager,
            IGuestShellFactory guestShellFactory,
            ISshKeyManager sshKeyManager,
            IKvpHeartbeatGate kvpHeartbeatGate,
            IGuestDiagnosticsCollector diagnosticsCollector,
            IPostBootCustomizationService postBootService,
            IPostBootStepPlanner stepPlanner,
            IHostNetworkService hostNetworkService,
            ILogger<DiskImageVmCreationStrategy> logger,
            IVmPathService pathService,
            IIsoBootCycleRunner isoBootRunner,
            ICloningIsoDownloader cloningIsoDownloader)
        {
            _lifecycleManager = lifecycleManager ?? throw new ArgumentNullException(nameof(lifecycleManager));
            _diskManager = diskManager ?? throw new ArgumentNullException(nameof(diskManager));
            _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
            _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _guestShellFactory = guestShellFactory ?? throw new ArgumentNullException(nameof(guestShellFactory));
            _sshKeyManager = sshKeyManager ?? throw new ArgumentNullException(nameof(sshKeyManager));
            _kvpHeartbeatGate = kvpHeartbeatGate ?? throw new ArgumentNullException(nameof(kvpHeartbeatGate));
            _diagnosticsCollector = diagnosticsCollector ?? throw new ArgumentNullException(nameof(diagnosticsCollector));
            _postBootService = postBootService ?? throw new ArgumentNullException(nameof(postBootService));
            _stepPlanner = stepPlanner ?? throw new ArgumentNullException(nameof(stepPlanner));
            _hostNetworkService = hostNetworkService ?? throw new ArgumentNullException(nameof(hostNetworkService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
            _isoBootRunner = isoBootRunner ?? throw new ArgumentNullException(nameof(isoBootRunner));
            _cloningIsoDownloader = cloningIsoDownloader ?? throw new ArgumentNullException(nameof(cloningIsoDownloader));
        }

        private readonly ICloningIsoDownloader _cloningIsoDownloader;

        public bool CanHandle(GalleryItem item, DiskImageFormat actualFileType)
        {
            if (item.IsNativeHyperV) return false;
            if (actualFileType == DiskImageFormat.Iso) return false;
            return true;
        }

        public async Task CreateVMAsync(VmCreationContext ctx)
        {
            ctx.Logger.Log($"Starting disk-image VM creation for {ctx.Plan.VmName}");

            VmDeploymentPlan plan = ctx.Plan;
            VmCustomizations vmCustomizations = ctx.Customizations;
            GalleryItem item = ctx.GalleryItem;
            string mediaPath = ctx.MediaResult.FinalMediaPath;
            string cloningIsoPath = plan.CloningIsoPath;
            var cancellationToken = ctx.CancellationToken;
            var progress = ctx.Progress;

            // Cloud-init gallery items (tag "cloud-init", e.g. Elastic SIEM)
            // are customized by a NoCloud seed ISO instead of the cloning ISO.
            bool isCloudInit = item.HasTag("cloud-init");
            string? seedIsoPath = null;
            // Held while the VM boots from the cloning ISO: the pre-boot
            // verification's read guard, disposed when this deployment ends.
            IIsoReadGuard? isoReadGuard = null;
            try
            {
            int detectedGeneration = ctx.MediaResult.VmGeneration;
            // Like every VMCreate deployment, the VM is Generation 2. A source
            // disk classified "BIOS" (no ESP) is handled by the stock cloning
            // cycle, which converts the layout to UEFI-bootable before first
            // boot; cloud-init items only ADD the NoCloud seed ISO so the
            // post-clone first boot provisions the automation user (the seed
            // replaces the cloning ISO's SSH-key injection).
            const int targetGen = 2;
            ctx.Logger.Log($"Detected generation {detectedGeneration}, targeting Gen {targetGen} (cloud-init: {isCloudInit})");

            progress.Report(new CreateVMProgressInfo { Phase = VmDeploymentPhase.CreateVM, DetectedGeneration = detectedGeneration });

            // Free-space pre-flight on the VM-disk drive, BEFORE any disk is
            // created: the MBR-clone flow writes a second full disk next to
            // the source, and running out mid-write makes Hyper-V PAUSE the
            // VM ("Disk Full", event 12635) — the deploy then fails as an
            // SSH timeout instead of a full-disk error. Fail fast here with
            // the real numbers instead.
            VmDiskSpaceGuard.EnsureAvailable(
                _pathService.DefaultVhdxPath,
                VmDiskSpaceGuard.RequiredBytes(mediaPath, cloneWritesSecondDisk: detectedGeneration == 1),
                _freeSpaceProbe);            _logger.LogInformation(
                "Free-space pre-flight passed for {VhdxPath} (generation {Gen}, media {Media})",
                _pathService.DefaultVhdxPath, detectedGeneration, mediaPath);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.CreateVMSkeleton));
            await _lifecycleManager.CreateVMAsync(plan, _pathService.DefaultVmPath, targetGen, cancellationToken);
            await _configManager.SetVMLoginNotes(plan, item.InitialUsername, item.InitialPassword, cancellationToken);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.ConnectNic));
            await _networkManager.ConnectNetworkAdapter(plan, cancellationToken);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.ConfigureHardware));
            await _configManager.SetCpuCount(plan, cancellationToken);
            await _configManager.DisableDynamicMemory(plan, cancellationToken);
            string secureBootTemplate = item.SecureBootTemplate ?? plan.SecureBootTemplate;
            await _bootManager.SetSecureBoot(plan, secureBootTemplate, cancellationToken);
            if (vmCustomizations.EnableIntegrationServices)
                await _configManager.EnableGuestServices(plan, cancellationToken);

            if (detectedGeneration == 2)
            {
                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachDisk));
                await _diskManager.AddExistingHardDrive(plan, mediaPath, cancellationToken);

                // Cloud-init items (UEFI-bootable cloud images, e.g. Rocky 10):
                // attach the NoCloud seed and boot the disk directly — no
                // cloning-ISO cycle. The seed carries the SSH key; cloud-init
                // provisions the automation user on first boot.
                if (isCloudInit)
                {
                    progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachBootDvd));
                    string publicKey = await _sshKeyManager.EnsureKeyPairAsync(cancellationToken);
                    seedIsoPath = Path.Combine(_pathService.DefaultVhdxPath, $"{plan.VmName}_cidata.iso");
                    CloudInitSeedBuilder.BuildSeedIso(seedIsoPath, plan.VmName, publicKey, _logger);
                    await _bootManager.AddBootDvd(plan, seedIsoPath, cancellationToken);
                    progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.SetBootOrder));
                    await _bootManager.SetFirstBootToHardDrive(plan, cancellationToken);
                }
                else
                {
                    bool needsIsoBoot = _stepPlanner.NeedsIsoBootCycle(item, vmCustomizations);

                    if (needsIsoBoot)
                    {
                        // The ISO is about to boot in this VM and drive
                        // root-level automation: re-verify the cached
                        // manifest's signature and the ISO's size and
                        // SHA-256 first, then HOLD a read handle so
                        // nothing can replace the verified bytes while
                        // the boot cycle runs. This runs offline too — a
                        // cache that fails the check never boots.
                        isoReadGuard = await _cloningIsoDownloader.VerifyCachedIsoGuardedAsync(plan, cancellationToken);
                        progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachBootDvd));
                        await _bootManager.AddBootDvd(plan, cloningIsoPath, cancellationToken);
                        progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.SetBootOrder));
                        await _bootManager.SetFirstBootToDvd(plan, cancellationToken);
                    }
                    else
                    {
                        progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.SetBootOrder));
                        await _bootManager.SetFirstBootToHardDrive(plan, cancellationToken);
                    }
                }
            }
            else
            {
                // "GPT_BIOS"/MBR source: the cloning ISO converts the layout to
                // UEFI-bootable on this Gen 2 VM. Cloud-init items get the
                // NoCloud seed ISO attached as a SECOND DVD: the clone cycle
                // boots first (DVD 1), then the cloned disk's first boot finds
                // the seed and provisions the automation user (replacing the
                // cloning ISO's SSH-key injection, which the cloud image's
                // cloud-init re-runs would overwrite anyway).
                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachDisk));
                await _diskManager.AddNewHardDrive(plan, _pathService.DefaultVhdxPath, cancellationToken);

                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachCloneDisk));
                await _diskManager.AddExistingHardDrive(plan, mediaPath, cancellationToken);
                _logger.LogInformation("Attached MBR disk as secondary for cloning: {MediaPath}", mediaPath);

                // Same pre-boot check on the MBR-clone path: the ISO is
                // about to boot and run the clone as root.
                isoReadGuard = await _cloningIsoDownloader.VerifyCachedIsoGuardedAsync(plan, cancellationToken);
                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachBootDvd));
                await _bootManager.AddBootDvd(plan, cloningIsoPath, cancellationToken);

                if (isCloudInit)
                {
                    string publicKey = await _sshKeyManager.EnsureKeyPairAsync(cancellationToken);
                    seedIsoPath = Path.Combine(_pathService.DefaultVhdxPath, $"{plan.VmName}_cidata.iso");
                    CloudInitSeedBuilder.BuildSeedIso(seedIsoPath, plan.VmName, publicKey, _logger);
                    await _bootManager.AddBootDvd(plan, seedIsoPath, cancellationToken);
                }

                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.SetBootOrder));
                await _bootManager.SetFirstBootToDvd(plan, cancellationToken);
            }

            if (plan.VirtualizationEnabled)
            {
                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.EnableNestedVirt));
                await _configManager.EnableVirtualization(plan, cancellationToken);
            }
            else
            {
                _logger.LogInformation("Virtualization extensions not enabled for VM: {VMName}", plan.VmName);
            }

            if (vmCustomizations.EnableIntegrationServices)
            {
                // Set the transport BEFORE the first start: Hyper-V only polls
                // the guest's vsock listener on a worker started with HvSocket;
                // switching a running VM does not arm the poller, so Enhanced
                // Session would stay unavailable until a host restart.
                // HvSocket is kept for Lamco: Hyper-V Enhanced Session
                // (vmconnect) connects through the guest's vsock listener
                // (CID-allowlisted to the host relay) and terminates TLS/CredSSP
                // on the host side. Note: Hyper-V's proprietary pre-RDP greeting
                // may still cause "InvalidContentType" on some client builds —
                // an IronRDP limitation. The TCP listener binds loopback ONLY
                // (auth_method=none must never face a network): standard mstsc
                // reaches it over an SSH port forward, e.g.
                //   ssh -L 3389:127.0.0.1:3389 <user>@<VM-IP>
                // then connect mstsc to 127.0.0.1:3389.
                await _configManager.SetEnhancedSession(plan, cancellationToken);
            }

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.StartVM));
            await _lifecycleManager.StartVM(plan, cancellationToken);

            bool needsPostBoot = GetPostBootSteps(item, vmCustomizations).Any();
            // The cloning-ISO cycle runs when the source layout needs
            // conversion (detectedGeneration == 1 means "BIOS/MBR-classified,
            // clone to UEFI") or when a non-cloud-init Gen 2 image requests
            // pre-boot ISO customization. UEFI-bootable cloud-init images
            // (detectedGeneration == 2) never enter the cycle — the seed
            // already carries their first-boot customization.
            bool needsIsoBootCycle = detectedGeneration == 1
                || (!isCloudInit && detectedGeneration == 2 && _stepPlanner.NeedsIsoBootCycle(item, vmCustomizations));
            bool needsCloudInitPostBoot = isCloudInit && needsPostBoot;

            if (needsIsoBootCycle)
            {
                var isoResult = await _isoBootRunner.RunAsync(
                    ctx, detectedGeneration, mediaPath, vmCustomizations, progress, cancellationToken);

                if (!isoResult.Success)
                {
                    throw new Exception($"ISO customization failed. {isoResult.ErrorMessage}");
                }
            }

            var postBootSteps = GetPostBootSteps(item, vmCustomizations).ToList();

            if (needsIsoBootCycle || needsCloudInitPostBoot)
            {
                if (postBootSteps.Count > 0)
                    progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.PostBoot));

                if (needsIsoBootCycle)
                {
                    progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.PostBoot, VmDeploymentSubStep.AddTempNic));
                    await _networkManager.RemoveTemporaryNetworkAdapter(plan, cancellationToken);
                    await _networkManager.AddTemporaryNetworkAdapter(plan, cancellationToken);

                    await _lifecycleManager.StartVM(plan, cancellationToken);
                }

                try
                {
                    string privateKeyPath = _sshKeyManager.GetPrivateKeyPath(vmCustomizations.CustomSshPublicKeyPath);
                    var shell = _guestShellFactory.Create(plan.VmName, privateKeyPath);

                    progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.PostBoot, VmDeploymentSubStep.WaitForSsh));
                    await shell.WaitForReadyAsync(cancellationToken);

                    // KVP heartbeat (integration-stack check): SSH being up
                    // proves the network stack, not the integration stack —
                    // an image can ship hv_kvp_daemon installed-but-unstarted,
                    // which would leave later integration (host↔guest KVP
                    // exchange, Enhanced Session) silently dead.
                    //
                    // Probe cheaply for an early signal, but do NOT gate
                    // here: InstallHypervDaemonsStep (post-boot, Order 280)
                    // is what installs and enables the daemon on images that
                    // ship it disabled — the Arch family ships all three
                    // Hyper-V daemons disabled — so a guest can legitimately
                    // show no heartbeat yet and be fully integrated a few
                    // minutes later. The hard gate after post-boot fails
                    // the deployment if the daemon is still silent then.
                    if (vmCustomizations.EnableIntegrationServices)
                    {
                        bool probeAlive = await _kvpHeartbeatGate.WaitForGuestKvpAsync(
                            plan.VmName, cancellationToken, timeoutSeconds: 1, pollIntervalMs: 1000);
                        if (probeAlive)
                        {
                            _logger.LogInformation(
                                "KVP heartbeat already visible for {VMName} after SSH — guest integration daemon is answering.",
                                plan.VmName);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "KVP heartbeat not yet visible for {VMName} after SSH — proceeding to post-boot, " +
                                "which installs and enables the Hyper-V daemons on images that ship them disabled " +
                                "(Arch family). The gate at the end of post-boot fails the deployment if the " +
                                "daemon is still not answering by then.",
                                plan.VmName);
                        }
                    }

                    if (isCloudInit)
                    {
                        // Confirm the seed ran so failures surface in the
                        // deployment log instead of only in the guest.
                        try
                        {
                            string cloudInitStatus = await shell.RunCommandAsync(
                                "cloud-init status --format json 2>/dev/null || sudo cloud-init status 2>/dev/null || echo '[cloud-init status unavailable]'",
                                cancellationToken);
                            _logger.LogInformation("cloud-init status for {VMName}: {Status}", plan.VmName, cloudInitStatus.Trim());
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to collect cloud-init status (non-fatal)");
                        }
                    }

                    if (postBootSteps.Count > 0)
                    {
                        // Split the post-boot pipeline around the hard KVP
                        // gate: everything up to (but excluding)
                        // DisableSshStep (Order 900) runs first, then the
                        // gate, then the restore tail. The gate's SSH
                        // witness probe needs a live SSH session — running
                        // it after the restore made the witness impossible
                        // on sshd-disabled images (PwnCloudOS: the probe
                        // got connection-refused from a healthy guest and
                        // the deploy falsely failed).
                        await _postBootService.RunLinuxPostBootAsync(
                            shell, plan, item, vmCustomizations, progress, cancellationToken,
                            upToOrderExclusive: PostBootStepPlanner.SshRestoreOrder,
                            chrootCompleted: ctx.ChrootCompletedSteps);
                    }

                    // Hard KVP gate — after post-boot proper, before the SSH
                    // restore tail. RunLinuxPostBootAsync (first phase) runs
                    // InstallHypervDaemonsStep (Order 280), which installs
                    // and enables hv_kvp_daemon on guests that ship it
                    // disabled (the Arch family ships all three Hyper-V
                    // daemons disabled), so waiting for post-boot to settle
                    // first is what lets such images pass instead of failing
                    // after the heartbeat timeout. If the daemon is still
                    // not answering even then, fail loudly with diagnostics
                    // instead of proceeding into a dead integration stack
                    // (the quickspawn lesson). Before failing, witness the
                    // daemon over the still-open SSH session: an answering
                    // daemon with empty HOST-side pools is a known first-
                    // boot artifact, not a dead pipeline. Still inside the
                    // try: the witness probe and diagnostics collection need
                    // the SSH shell and the temp NIC the finally removes.
                    if (vmCustomizations.EnableIntegrationServices)
                    {
                        progress.Report(CreateVMProgressInfo.ForPhase(
                            VmDeploymentPhase.PostBoot, VmDeploymentSubStep.WaitForKvpHeartbeat));
                        var gateOutcome = await WaitForKvpGateRacingWitnessAsync(shell, plan.VmName, cancellationToken);
                        if (gateOutcome == KvpGateRace.Outcome.TimedOut)
                        {
                            // The gate watches the HOST-side pools, which can
                            // lag the guest daemon by many minutes on first
                            // boot (observed live: guest answering ~4 s after
                            // SSH, pools still empty after 120 s, filled
                            // ~19 min after boot). "Unconfirmed" is therefore
                            // not "dead": witness the daemon over the still-
                            // open SSH session before failing — an answering
                            // daemon means the pipeline is healthy and the
                            // empty pools are a host artifact.
                            string gateState =
                                _kvpHeartbeatGate.LastDiagnostics?.ToString()
                                ?? "no gate diagnostics available";
                            KvpWitnessResult witness = await TryConfirmGuestKvpDaemonAsync(
                                shell, cancellationToken);
                            if (witness == KvpWitnessResult.Confirmed)
                            {
                                _logger.LogWarning(
                                    "KVP pools stayed empty for {VMName} ({GateState}) even after post-boot, but " +
                                    "the guest's KVP daemon is running (SSH witness probe). This matches the known " +
                                    "host-side first-boot pool materialization lag — proceeding with deployment. " +
                                    "Host↔guest KVP exchange may take a few more minutes to become usable in " +
                                    "this VM. (The gate timeout is a caller-visible default; the witness " +
                                    "prevents failing a verifiably healthy deployment.)",
                                    plan.VmName, gateState);
                            }
                            else
                            {
                                var diagnostics = await _diagnosticsCollector.CollectAsync(
                                    plan.VmName, cancellationToken,
                                    _sshKeyManager.GetPrivateKeyPath(vmCustomizations.CustomSshPublicKeyPath));
                                _logger.LogError(
                                    "KVP heartbeat not confirmed for {VMName} ({GateState}) and the guest's KVP " +
                                    "daemon is not running. Hyper-V integration daemon binaries: hv_kvp_daemon " +
                                    "(Debian- and RHEL-class), unit hypervkvpd (RHEL-class, static) or " +
                                    "hv-kvp-daemon (Debian). Guest diagnostics:\n{Diagnostics}",
                                    plan.VmName, gateState, diagnostics.RawOutput);
                                throw new InvalidOperationException(
                                    witness == KvpWitnessResult.Unreachable
                                        ? $"The Hyper-V KVP heartbeat for VM '{plan.VmName}' was not confirmed " +
                                          $"({gateState}) and the witness probe could not reach the guest over " +
                                          $"SSH (transport error). The guest may have rebooted or lost its " +
                                          $"network mid-deployment. Guest diagnostics:\n{diagnostics.RawOutput}"
                                        : $"The Hyper-V KVP heartbeat for VM '{plan.VmName}' was not confirmed " +
                                          $"({gateState}) and the guest's KVP integration daemon (binary " +
                                          $"hv_kvp_daemon, unit hypervkvpd on RHEL-class — hv-kvp-daemon on Debian) " +
                                          $"is not running. The image may ship it unstarted: on Debian-class " +
                                          $"guests start it with 'systemctl enable --now hv-kvp-daemon'; on " +
                                          $"RHEL-class guests the unit is static by design — check it with " +
                                          $"'systemctl status hypervkvpd'. If it is active, the HOST-side KVP pool " +
                                          $"may simply lag on first boot — wait a few minutes and retry the " +
                                          $"deployment, or disable integration services for this VM.");
                            }
                        }
                        else if (gateOutcome == KvpGateRace.Outcome.PoolConfirmed)
                        {
                            _logger.LogInformation("KVP heartbeat confirmed for {VMName} — guest integration daemon is answering.", plan.VmName);
                        }
                        // WitnessConfirmed already logged its early-proceed
                        // warning inside the race helper.
                    }

                    // SSH-restore tail (DisableSshStep, Order 900): put
                    // sshd-disabled images back into their shipped state.
                    // Runs AFTER the gate so the witness probe above still
                    // has a live session to ask.
                    if (postBootSteps.Count > 0)
                    {
                        await _postBootService.RunLinuxPostBootAsync(
                            shell, plan, item, vmCustomizations, progress, cancellationToken,
                            fromOrderInclusive: PostBootStepPlanner.SshRestoreOrder,
                            chrootCompleted: ctx.ChrootCompletedSteps);
                    }
                }
                finally
                {
                    if (needsIsoBootCycle)
                    {
                        try
                        {
                            await _networkManager.RemoveTemporaryNetworkAdapter(plan, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to remove temporary network adapter (non-fatal)");
                        }
                    }

                    if (seedIsoPath != null)
                    {
                        try
                        {
                            await _bootManager.RemoveBootDvd(plan, seedIsoPath, cancellationToken);
                            if (File.Exists(seedIsoPath))
                                File.Delete(seedIsoPath);
                            _logger.LogInformation("Cleaned up cloud-init seed ISO for VM {VMName}", plan.VmName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to clean up cloud-init seed ISO for VM {VMName}", plan.VmName);
                        }
                    }
                }
            }
            else if (isCloudInit && seedIsoPath != null)
            {
                // No post-boot steps ran, so we never waited for the guest:
                // leave the seed attached rather than risk ejecting it
                // mid-first-boot. It is a tiny read-only DVD and cloud-init
                // ignores it on subsequent boots.
                _logger.LogInformation("Cloud-init seed ISO left attached to VM {VMName} (no post-boot steps)", plan.VmName);
            }
        }
        finally
        {
            // The boot cycle is over (the runner removed the clone DVD);
            // release the read guard on the verified ISO.
            isoReadGuard?.Dispose();
        }
        }

        private IEnumerable<ICustomizationStep?> GetPostBootSteps(GalleryItem item, VmCustomizations customizations)
        {
            // The single null element is intentional: it signals the deploy pipeline
            // that a post-boot phase exists without pinning a concrete step here.
            return _postBootService.HasLinuxPostBootSteps(item, customizations)
                ? new ICustomizationStep?[] { null }
                : Array.Empty<ICustomizationStep?>();
        }

        /// <summary>
        /// Witness probe for a KVP gate timeout: asks the guest itself, over the
        /// already-open shell, whether the KVP daemon runs. This distinguishes
        /// the timeout classes — daemon absent in the guest (a real image
        /// defect, <see cref="KvpWitnessResult.Absent"/>), daemon alive while
        /// the host-side pools lag (a host artifact on first boot,
        /// <see cref="KvpWitnessResult.Confirmed"/>), or the SSH session itself
        /// broken (<see cref="KvpWitnessResult.Unreachable"/>). The probe runs
        /// BEFORE DisableSshStep (Order 900) restores sshd-disabled images, so
        /// a closed SSH path here is a real transport failure, not the
        /// restore — every outcome other than Confirmed fails the deployment.
        /// </summary>
        private enum KvpWitnessResult { Confirmed, Absent, Unreachable }

        /// <summary>
        /// <summary>
        /// How long the pool gate polls before the witness probe re-runs.
        /// The first witness probe happens immediately (the guest daemon
        /// answers SSH within seconds of boot; pools can lag it by many
        /// minutes on first boot), so this grace only matters when that
        /// first probe came back silent — e.g. the daemon was still starting.
        /// </summary>
        private const int KvpWitnessGraceSeconds = 60;

        /// <summary>
        /// Races the pool gate against the delayed SSH witness probe
        /// (<see cref="KvpGateRace"/>). Early witness-confirmed proceeds are
        /// logged here so the deploy log carries the evidence for them.
        /// </summary>
        private async Task<KvpGateRace.Outcome> WaitForKvpGateRacingWitnessAsync(
            IGuestShell shell, string vmName, CancellationToken cancellationToken)
        {
            var outcome = await KvpGateRace.RunAsync(
                gateToken => _kvpHeartbeatGate.WaitForGuestKvpAsync(vmName, gateToken),
                async ct => await TryConfirmGuestKvpDaemonAsync(shell, ct) == KvpWitnessResult.Confirmed,
                TimeSpan.FromSeconds(KvpWitnessGraceSeconds),
                cancellationToken);

            if (outcome == KvpGateRace.Outcome.WitnessConfirmed)
            {
                _logger.LogWarning(
                    "KVP pools unreadable for {VMName}, but the guest's KVP daemon is running (SSH witness probe) — " +
                    "proceeding instead of waiting out the gate budget. Host-side pool materialization can lag first " +
                    "boot by many minutes; the exchange becomes usable later.",
                    vmName);
            }

            return outcome;
        }

        private async Task<KvpWitnessResult> TryConfirmGuestKvpDaemonAsync(IGuestShell shell, CancellationToken cancellationToken)
        {
            const string runningToken = "VMCREATE_KVP_DAEMON_RUNNING";
            try
            {
                // Must ALWAYS exit 0: IGuestShell.RunCommandAsync throws on a
                // non-zero exit code, so presence/absence is reported on stdout
                // instead. Covers both naming schemes — binary hv_kvp_daemon
                // (Debian- and RHEL-class hyperv-daemons package), unit
                // hypervkvpd (RHEL-class) or hv-kvp-daemon (Debian).
                string stdout = await shell.RunCommandAsync(
                    "if pgrep -x hv_kvp_daemon >/dev/null 2>&1" +
                    " || pgrep -x hypervkvpd >/dev/null 2>&1" +
                    " || systemctl is-active --quiet hypervkvpd hv-kvp-daemon hv_kvp_daemon 2>/dev/null" +
                    $"; then echo {runningToken}; else echo VMCREATE_KVP_DAEMON_ABSENT; fi",
                    cancellationToken);
                return !string.IsNullOrEmpty(stdout) &&
                       stdout.Contains(runningToken, StringComparison.Ordinal)
                    ? KvpWitnessResult.Confirmed
                    : KvpWitnessResult.Absent;
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       && SshGuestShell.IsSshTransportError(ex))
            {
                // Transport failure only (connection refused/reset, broken
                // pipe, session ended — the classes IsSshTransportError
                // knows): the SSH session itself is gone. A command timeout
                // or non-zero exit does NOT match the filter and rethrows —
                // those are fail-safe, exactly like the old contract.
                _logger.LogWarning(ex, "KVP daemon witness probe over SSH hit a transport error — the session broke before the guest could answer");
                return KvpWitnessResult.Unreachable;
            }
        }
    }
}
