using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    public interface IVmCreator
    {
        Task CreateVMAsync(VmSettings vmSettings, VmCustomizations vmCustomizations, string extractPath, GalleryItem galleryItem, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> downloadProgressInfo);
    }

    public class HyperVVmCreator : IVmCreator
    {
        private readonly string _defaultVhdxPath;
        private readonly IMediaHandlerFactory _mediaHandlerFactory;
        private readonly IHyperVManager _hyperVManager;
        private readonly ILogger<HyperVVmCreator> _logger;
        private readonly ISshKeyManager _sshKeyManager;
        private readonly IKvpSender _kvpSender;
        private readonly IKvpPoller _kvpPoller;
        private readonly IVmShutdownWatcher _shutdownWatcher;
        private readonly IGuestDiagnosticsCollector _diagnosticsCollector;
        private readonly IGuestShellFactory _guestShellFactory;
        private readonly IEnumerable<ICustomizationStep> _customizationSteps;
        private const int OriginalDiskScsiControllerLocation = 1;
        public HyperVVmCreator(
            ILogger<HyperVVmCreator> logger,
            IMediaHandlerFactory mediaHandlerFactory,
            IHyperVManager hyperVManager,
            IEnumerable<ICustomizationStep> customizationSteps,
            ISshKeyManager sshKeyManager,
            IKvpSender kvpSender,
            IKvpPoller kvpPoller,
            IVmShutdownWatcher shutdownWatcher,
            IGuestDiagnosticsCollector diagnosticsCollector,
            IGuestShellFactory guestShellFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mediaHandlerFactory = mediaHandlerFactory ?? throw new ArgumentNullException(nameof(mediaHandlerFactory));
            _hyperVManager = hyperVManager ?? throw new ArgumentNullException(nameof(hyperVManager));
            _customizationSteps = customizationSteps ?? Array.Empty<ICustomizationStep>();
            _sshKeyManager = sshKeyManager ?? throw new ArgumentNullException(nameof(sshKeyManager));
            _kvpSender = kvpSender ?? throw new ArgumentNullException(nameof(kvpSender));
            _kvpPoller = kvpPoller ?? throw new ArgumentNullException(nameof(kvpPoller));
            _shutdownWatcher = shutdownWatcher ?? throw new ArgumentNullException(nameof(shutdownWatcher));
            _diagnosticsCollector = diagnosticsCollector ?? throw new ArgumentNullException(nameof(diagnosticsCollector));
            _guestShellFactory = guestShellFactory ?? throw new ArgumentNullException(nameof(guestShellFactory));
            _defaultVhdxPath = GetDefaultVirtualHardDiskPath();
        }

        private string GetDefaultVirtualHardDiskPath()
        {
            string defaultPath = @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks";
            try
            {
                string registryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(registryPath))
                {
                    if (key != null)
                    {
                        string path = key.GetValue("DefaultVirtualHardDiskPath") as string;
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                        {
                            _logger.LogInformation("Using DefaultVirtualHardDiskPath from registry: {Path}", path);
                            return path;
                        }
                    }
                }
                _logger.LogInformation("DefaultVirtualHardDiskPath not found or invalid. Using default: {DefaultPath}", defaultPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading DefaultVirtualHardDiskPath: {Message}", ex.Message);
            }
            return defaultPath;
        }

        public async Task CreateVMAsync(VmSettings vmSettings, VmCustomizations vmCustomizations, string sourceFile, GalleryItem item, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> createVMProgressInfo)
        {
            try
            {
                _logger.LogInformation("Starting VM creation for {VMName}", vmSettings.VMName);

                // Replace previous VM if requested
                if (vmSettings.ReplacePreviousVm)
                {
                    await ReplacePreviousVmAsync(vmSettings, cancellationToken);
                }
                    
                // Determine the media type from the actual file on disk rather than
                // the gallery item's DiskUri.  After extraction the sourceFile points at
                // the real disk (e.g. a .vmdk extracted from an OVA).
                string actualFileType = DiskFileDetector.DetectFileType(sourceFile);
                if (actualFileType is "Unknown" or "Other")
                    actualFileType = item.FileType;  // fallback to gallery metadata

                IMediaHandler mediaHandler = _mediaHandlerFactory.CreateHandler(actualFileType);
                string mediaPath = await mediaHandler.PrepareMediaAsync(sourceFile, _defaultVhdxPath, vmSettings, item, createVMProgressInfo, cancellationToken);

                bool isIsoMedia = mediaHandler is IsoMediaHandler;

                if (isIsoMedia)
                {
                    // ── ISO installer flow ─────────────────────────────────────
                    // The downloaded file is an ISO image. We create an empty VHDX
                    // for the OS to install onto, attach the ISO as a DVD, and boot
                    // from the DVD so the user can run the installer interactively.
                    const int targetGeneration = 2;

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM" });

                    await _hyperVManager.CreateVMAsync(vmSettings, _defaultVhdxPath, targetGeneration, cancellationToken);
                    await _hyperVManager.SetVMLoginNotes(vmSettings, item.InitialUsername, item.InitialPassword, cancellationToken);
                    await _hyperVManager.ConnectNetworkAdapter(vmSettings, cancellationToken);

                    await _hyperVManager.SetCpuCount(vmSettings, cancellationToken);
                    await _hyperVManager.DisableDynamicMemory(vmSettings, cancellationToken);
                    await _hyperVManager.SetSecureBoot(vmSettings, cancellationToken);
                    if (vmCustomizations.EnableIntegrationServices)
                        await _hyperVManager.EnableGuestServices(vmSettings, cancellationToken);

                    // Create an empty boot disk for the installer to target
                    await _hyperVManager.AddNewHardDrive(vmSettings, _defaultVhdxPath, cancellationToken);

                    // Attach the ISO as a DVD drive and boot from it
                    await _hyperVManager.AddBootDvd(vmSettings, mediaPath, cancellationToken);
                    await _hyperVManager.SetFirstBootToDvd(vmSettings, cancellationToken);

                    if (vmSettings.VirtualizationEnabled)
                        await _hyperVManager.EnableVirtualization(vmSettings, cancellationToken);

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "StartVM" });
                    await _hyperVManager.StartVM(vmSettings, cancellationToken);

                    if (vmCustomizations.EnableIntegrationServices)
                        await _hyperVManager.SetEnhancedSession(vmSettings, cancellationToken);

                    // ISO flow is done — the user completes the installation interactively.
                    return;
                }

                // ── Disk-image flow (VMDK / QCOW2 / VHDX) ────────────────────
                string cloningIsoPath = vmSettings.CloningIsoPath;
                int detectedGeneration = mediaHandler.VmGeneration; // 1 for MBR, 2 for GPT
                const int targetGen = 2; // Always target Gen 2

                // Report detected generation so the UI can insert MBR-specific cards
                createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", DetectedGeneration = detectedGeneration.ToString() });

                await _hyperVManager.CreateVMAsync(vmSettings, _defaultVhdxPath, targetGen, cancellationToken);
                await _hyperVManager.SetVMLoginNotes(vmSettings, item.InitialUsername, item.InitialPassword, cancellationToken);
                //await _hyperVManager.AddNetworkAdapter(vmSettings, cancellationToken);
                await _hyperVManager.ConnectNetworkAdapter(vmSettings, cancellationToken);

                // Common settings: CPU, enhanced session, secure boot
                await _hyperVManager.SetCpuCount(vmSettings, cancellationToken);
                await _hyperVManager.DisableDynamicMemory(vmSettings, cancellationToken);
                await _hyperVManager.SetSecureBoot(vmSettings, cancellationToken);
                if (vmCustomizations.EnableIntegrationServices)
                    await _hyperVManager.EnableGuestServices(vmSettings, cancellationToken);

                if (detectedGeneration == 2)
                {
                    // Drive is GPT partitioned: Attach media directly as primary boot disk
                    await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);

                    bool needsIsoBoot = vmCustomizations.ConfigureXrdp
                        || vmCustomizations.ConfigureHtbVpn
                        || vmCustomizations.SyncTimezone;

                    if (needsIsoBoot)
                    {
                        // GPT + customization: boot from customization ISO to chroot-install packages
                        await _hyperVManager.AddBootDvd(vmSettings, cloningIsoPath, cancellationToken);
                        await _hyperVManager.SetFirstBootToDvd(vmSettings, cancellationToken);
                    }
                    else
                    {
                        await _hyperVManager.SetFirstBootToHardDrive(vmSettings, cancellationToken);
                    }
                }
                else if (detectedGeneration == 1)
                {
                    // Drive is MBR partitioned: Add a new (larger) drive first so that we can copy data from old drive
                    await _hyperVManager.AddNewHardDrive(vmSettings, _defaultVhdxPath, cancellationToken);

                    // Attach old disk
                    await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);
                    _logger.LogInformation("Attached MBR disk as secondary for cloning: {MediaPath}", mediaPath);

                    // Attach cloning ISO and set as first boot device
                    await _hyperVManager.AddBootDvd(vmSettings, cloningIsoPath, cancellationToken);

                    // Set ISO as first boot (for one-time clone)
                    await _hyperVManager.SetFirstBootToDvd(vmSettings, cancellationToken);
                }
                else
                {
                    throw new Exception($"Unsupported generation detected: {detectedGeneration}");
                }

                if (vmSettings.VirtualizationEnabled)
                {
                    await _hyperVManager.EnableVirtualization(vmSettings, cancellationToken);
                }
                else
                {
                    _logger.LogInformation("Virtualization extensions not enabled for VM: {VMName}", vmSettings.VMName);
                }

                createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "StartVM" });
                await _hyperVManager.StartVM(vmSettings, cancellationToken);

                bool needsPostBoot = _customizationSteps
                    .Any(s => s.Phase == CustomizationPhase.PostBoot && s.IsApplicable(item, vmCustomizations));
                bool needsIsoBootCycle = detectedGeneration == 1
                    || (detectedGeneration == 2 && (vmCustomizations.ConfigureXrdp || needsPostBoot));

                if (needsIsoBootCycle)
                {
                    // Report a Customize phase for Gen2 builds so the UI shows progress
                    if (detectedGeneration == 2)
                    {
                        createVMProgressInfo.Report(new CreateVMProgressInfo
                        {
                            Phase = "Customize",
                            DetectedGeneration = "2"
                        });
                    }

                    // ── KVP corruption mitigation ───────────────────────────────
                    // When a Gen2 VM boots, the Hyper-V host pushes network
                    // configuration (IP addresses, DNS, IPv6 multicast prefixes)
                    // through the same VMBus KVP channel that AddKvpItems uses.
                    // Both streams land in the guest's .kvp_pool_0 as fixed-size
                    // 2560-byte records (512 key + 2048 value).  If our WMI writes
                    // overlap with the network config burst the records get
                    // corrupted — e.g. key "DUMMY" becomes "DUMMYcastprefix" with
                    // ff02:: multicast data in the value field.
                    //
                    // This is NOT purely a timing issue — the first two records
                    // written via AddKvpItems are consistently corrupted even with
                    // a 10s delay, likely because hv_kvp_daemon is inactive and the
                    // kernel hv_utils module doesn't properly serialize VMBus writes
                    // across pools.
                    //
                    // Mitigation: wait 10s for boot to settle, then send two
                    // throwaway KVPs so real keys land in record slot 3+ where
                    // corruption doesn't reach.  The guest autorun.sh also retries
                    // reading VMCREATE_MODE for up to 30 seconds as belt-and-suspenders.
                    // ────────────────────────────────────────────────────────────
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

                    await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "PADDING_1", "true", cancellationToken);
                    await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "PADDING_2", "true", cancellationToken);

                    // ── SSH key first ────────────────────────────────────────
                    // Send the SSH public key as early as possible so the ISO's
                    // inject-ssh-key.service can install it before autorun starts.
                    // This lets us SSH in to debug even when the main workflow hangs.
                    await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "PADDING_3", "true", cancellationToken);

                    string sshPublicKey;
                    if (!string.IsNullOrEmpty(vmCustomizations.CustomSshPublicKeyPath))
                        sshPublicKey = await _sshKeyManager.ReadPublicKeyAsync(vmCustomizations.CustomSshPublicKeyPath, cancellationToken);
                    else
                        sshPublicKey = await _sshKeyManager.EnsureKeyPairAsync(cancellationToken);

                    _logger.LogInformation("Sending SSH public key ({Length} chars) via KVP to VM {VMName}",
                        sshPublicKey?.Length ?? 0, vmSettings.VMName);
                    await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_SSH_PUBKEY", sshPublicKey, cancellationToken);

                    // ── Workflow flags ────────────────────────────────────────
#if DEBUG
                    //await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_DEBUG", "true", cancellationToken);
#endif
                    if (detectedGeneration == 2)
                    {
                        // Tell the ISO to run customize-only mode (skip disk cloning)
                        await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_MODE", "customize", cancellationToken);
                    }

                    if (vmCustomizations.ConfigureXrdp)
                    {
                        await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_XRDP", "true", cancellationToken);

                        if (!string.IsNullOrEmpty(item.InitialUsername))
                            await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_XRDP_USERNAME", item.InitialUsername, cancellationToken);
                    }

                    // ── Monitor ISO progress and wait for shutdown ─────────────
                    const int shutdownTimeoutSeconds = 600;
                    bool shutDown;

                    if (detectedGeneration == 1)
                    {
                        // Gen1: Monitor partclone disk clone progress, then wait for shutdown.
                        // PollKVPForProgressAsync now has a timeout and VM-shutdown detection
                        // so we never hang indefinitely if the completion KVP is missed.
                        bool cloneMarkerSeen = await _kvpPoller.PollKVPForProgressAsync(
                            vmSettings.VMName, createVMProgressInfo, cancellationToken, shutdownTimeoutSeconds);

                        // Partclone done (or timed out) — transition from CloneDisk to Customize phase
                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "Customize" });

                        if (cloneMarkerSeen)
                        {
                            // Clone completed — wait for the remaining customization + shutdown
                            shutDown = await _shutdownWatcher.WaitForVMShutdownAsync(vmSettings.VMName, cancellationToken, shutdownTimeoutSeconds);
                        }
                        else
                        {
                            // Timeout or VM already shut down during clone polling —
                            // check once with a 0s timeout to see current state.
                            shutDown = await _shutdownWatcher.WaitForVMShutdownAsync(vmSettings.VMName, cancellationToken, timeoutSeconds: 1);
                        }
                    }
                    else
                    {
                        // Gen2 customize-only: no partclone step.
                        // Poll WorkflowProgress KVP while waiting for the VM to shut
                        // itself down via OnSuccess=poweroff.target.
                        shutDown = await _kvpPoller.WaitForShutdownWithProgressAsync(
                            vmSettings.VMName, createVMProgressInfo, cancellationToken, shutdownTimeoutSeconds);
                    }

                    if (!shutDown)
                    {
                        _logger.LogWarning("VM {VMName} did not shut down within {Timeout}s — collecting diagnostics.", vmSettings.VMName, shutdownTimeoutSeconds);

                        // Collect diagnostics from the ISO guest via PowerShell Direct
                        var diagnostics = await _diagnosticsCollector
                            .CollectAsync(vmSettings.VMName, cancellationToken,
                                _sshKeyManager.GetPrivateKeyPath(vmCustomizations.CustomSshPublicKeyPath));

                        _logger.LogError("Guest diagnostics for {VMName}: {Summary}\n{RawOutput}",
                            vmSettings.VMName, diagnostics.Summary, diagnostics.RawOutput);

                        // Force-stop the stuck VM
                        await _hyperVManager.StopVMAsync(vmSettings.VMName, cancellationToken);
                        _logger.LogInformation("Force-stopped VM {VMName} after timeout.", vmSettings.VMName);

                        // Report the failure to the UI — this will set the phase card to Failed
                        createVMProgressInfo.Report(new CreateVMProgressInfo
                        {
                            Phase = "Customize",
                            ErrorMessage = diagnostics.Summary,
                            DiagnosticsLog = diagnostics.RawOutput
                        });

                        throw new Exception($"ISO customization timed out. {diagnostics.Summary}");
                    }

                    if (detectedGeneration == 1)
                    {
                        // Remove original disk
                        await _hyperVManager.RemoveHardDrive(vmSettings, OriginalDiskScsiControllerLocation, cancellationToken);
                    }

                    // Remove ISO
                    await _hyperVManager.RemoveBootDvd(vmSettings, cloningIsoPath, cancellationToken);

                    // Set hard drive as first boot device now that DVD and old disk are removed
                    await _hyperVManager.SetFirstBootToHardDrive(vmSettings, cancellationToken);
                }
                if (vmCustomizations.EnableIntegrationServices)
                    await _hyperVManager.SetEnhancedSession(vmSettings, cancellationToken);

                // ── Post-boot: collect autorun log + run step pipeline ────
                var postBootSteps = _customizationSteps
                    .Where(s => s.Phase == CustomizationPhase.PostBoot && s.IsApplicable(item, vmCustomizations))
                    .OrderBy(s => s.Order)
                    .ToList();

                // After the ISO boot cycle the VM is off. Start it from the
                // hard drive so we can collect the autorun log and run any
                // post-boot customization steps.
                if (needsIsoBootCycle)
                {
                    if (postBootSteps.Count > 0)
                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "PostBoot" });

                    await _hyperVManager.StartVM(vmSettings, cancellationToken);

                    string privateKeyPath = _sshKeyManager.GetPrivateKeyPath(vmCustomizations.CustomSshPublicKeyPath);
                    var shell = _guestShellFactory.Create(vmSettings.VMName, privateKeyPath);

                    await shell.WaitForReadyAsync(cancellationToken);

                    // Collect the autorun log saved by the ISO's customize script
                    try
                    {
                        string autorunLog = await shell.RunCommandAsync(
                            "sudo cat /var/log/vmcreate-autorun.log 2>/dev/null || echo '[no autorun log found]'",
                            cancellationToken);
                        _logger.LogInformation("Autorun log for {VMName}:\n{Log}", vmSettings.VMName, autorunLog);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to collect autorun log (non-fatal)");
                    }

                    // Run post-boot customization steps
                    if (postBootSteps.Count > 0)
                    {
                        int completed = 0;
                        foreach (var step in postBootSteps)
                        {
                            _logger.LogInformation("Running post-boot step: {StepName} (order {Order})", step.Name, step.Order);
                            createVMProgressInfo.Report(new CreateVMProgressInfo
                            {
                                Phase = "PostBoot",
                                ProgressPercentage = (int)((double)completed / postBootSteps.Count * 100),
                                StepName = step.Name
                            });

                            await step.ExecuteAsync(shell, item, vmCustomizations, _logger, cancellationToken);

                            completed++;
                            _logger.LogInformation("Completed post-boot step: {StepName}", step.Name);
                        }

                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "PostBoot", ProgressPercentage = 100 });
                    }

                    // VM is running from the hard drive with all customizations applied.
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating VM: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Finds VMs whose name matches the base name (before the _timestamp suffix),
        /// stops them, collects their VHDX paths, removes the VMs, then deletes the VHDX files.
        /// </summary>
        private async Task ReplacePreviousVmAsync(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            // The base name is the VMName before DeployPage appends _yyyyMMddHHmmss.
            // At this point VMName already has the timestamp, so strip it.
            string fullName = vmSettings.VMName;
            string baseName = fullName;
            int lastUnderscore = fullName.LastIndexOf('_');
            if (lastUnderscore > 0 && fullName.Length - lastUnderscore - 1 == 14)
            {
                // Looks like _yyyyMMddHHmmss
                baseName = fullName.Substring(0, lastUnderscore);
            }

            _logger.LogInformation("Looking for existing VMs matching base name: {BaseName}", baseName);
            string[] existingVms = await _hyperVManager.FindExistingVmsByBaseNameAsync(baseName, cancellationToken);

            foreach (var existingVmName in existingVms)
            {
                // Don't remove the VM we're about to create
                if (string.Equals(existingVmName, fullName, StringComparison.OrdinalIgnoreCase))
                    continue;

                _logger.LogInformation("Replacing existing VM: {ExistingVMName}", existingVmName);

                // Collect VHDX paths before removing the VM
                string[] vhdxPaths = await _hyperVManager.GetVmHardDiskPathsAsync(existingVmName, cancellationToken);

                // Stop and remove the VM
                await _hyperVManager.StopVMAsync(existingVmName, cancellationToken);
                await _hyperVManager.RemoveVMAsync(existingVmName, cancellationToken);

                // Delete VHDX files
                foreach (string vhdxPath in vhdxPaths)
                {
                    try
                    {
                        if (File.Exists(vhdxPath))
                        {
                            File.Delete(vhdxPath);
                            _logger.LogInformation("Deleted old VHDX: {VhdxPath}", vhdxPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete old VHDX {VhdxPath}: {Message}", vhdxPath, ex.Message);
                    }
                }

                // Also try to remove the VM's configuration folder (Hyper-V creates a folder under the VHD path)
                string vmConfigFolder = Path.Combine(_defaultVhdxPath, existingVmName);
                try
                {
                    if (Directory.Exists(vmConfigFolder))
                    {
                        Directory.Delete(vmConfigFolder, true);
                        _logger.LogInformation("Deleted old VM config folder: {Folder}", vmConfigFolder);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete VM config folder {Folder}: {Message}", vmConfigFolder, ex.Message);
                }
            }
        }
    }
}