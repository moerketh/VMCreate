using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
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
    internal class DiskImageVmCreationStrategy : IVmCreationStrategy
    {
        private readonly IHyperVManager _hyperVManager;
        private readonly IGuestShellFactory _guestShellFactory;
        private readonly ISshKeyManager _sshKeyManager;
        private readonly IKvpSender _kvpSender;
        private readonly IKvpPoller _kvpPoller;
        private readonly IVmShutdownWatcher _shutdownWatcher;
        private readonly IGuestDiagnosticsCollector _diagnosticsCollector;
        private readonly IPostBootCustomizationService _postBootService;
        private readonly IHostNetworkService _hostNetworkService;
        private readonly IVmPathService _pathService;
        private readonly ILogger<DiskImageVmCreationStrategy> _logger;
        private const int OriginalDiskScsiControllerLocation = 1;

        public DiskImageVmCreationStrategy(
            IHyperVManager hyperVManager,
            IGuestShellFactory guestShellFactory,
            ISshKeyManager sshKeyManager,
            IKvpSender kvpSender,
            IKvpPoller kvpPoller,
            IVmShutdownWatcher shutdownWatcher,
            IGuestDiagnosticsCollector diagnosticsCollector,
            IPostBootCustomizationService postBootService,
            IHostNetworkService hostNetworkService,
            ILogger<DiskImageVmCreationStrategy> logger,
            IVmPathService pathService)
        {
            _hyperVManager = hyperVManager ?? throw new ArgumentNullException(nameof(hyperVManager));
            _guestShellFactory = guestShellFactory ?? throw new ArgumentNullException(nameof(guestShellFactory));
            _sshKeyManager = sshKeyManager ?? throw new ArgumentNullException(nameof(sshKeyManager));
            _kvpSender = kvpSender ?? throw new ArgumentNullException(nameof(kvpSender));
            _kvpPoller = kvpPoller ?? throw new ArgumentNullException(nameof(kvpPoller));
            _shutdownWatcher = shutdownWatcher ?? throw new ArgumentNullException(nameof(shutdownWatcher));
            _diagnosticsCollector = diagnosticsCollector ?? throw new ArgumentNullException(nameof(diagnosticsCollector));
            _postBootService = postBootService ?? throw new ArgumentNullException(nameof(postBootService));
            _hostNetworkService = hostNetworkService ?? throw new ArgumentNullException(nameof(hostNetworkService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
        }

        public bool CanHandle(GalleryItem item, string actualFileType)
        {
            // Any non-ISO, non-native-Hyper-V disk image is handled here.
            if (item.IsNativeHyperV) return false;
            if (string.Equals(actualFileType, "ISO", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public async Task CreateVMAsync(VmCreationContext ctx)
        {
            VmSettings vmSettings = ctx.Settings;
            VmCustomizations vmCustomizations = ctx.Customizations;
            GalleryItem item = ctx.GalleryItem;
            string mediaPath = ctx.MediaPath;
            string cloningIsoPath = vmSettings.CloningIsoPath;
            var cancellationToken = ctx.CancellationToken;
            var progress = ctx.Progress;

            int detectedGeneration = ctx.MediaHandler.VmGeneration;
            const int targetGen = 2;

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", DetectedGeneration = detectedGeneration.ToString() });

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_CreateVMSkeleton" });
            await _hyperVManager.CreateVMAsync(vmSettings, _pathService.DefaultVmPath, targetGen, cancellationToken);
            await _hyperVManager.SetVMLoginNotes(vmSettings, item.InitialUsername, item.InitialPassword, cancellationToken);

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConnectNic" });
            await _hyperVManager.ConnectNetworkAdapter(vmSettings, cancellationToken);

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConfigureHardware" });
            await _hyperVManager.SetCpuCount(vmSettings, cancellationToken);
            await _hyperVManager.DisableDynamicMemory(vmSettings, cancellationToken);
            await _hyperVManager.SetSecureBoot(vmSettings, cancellationToken);
            if (vmCustomizations.EnableIntegrationServices)
                await _hyperVManager.EnableGuestServices(vmSettings, cancellationToken);

            if (detectedGeneration == 2)
            {
                progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachDisk" });
                await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);

                bool needsIsoBoot = vmCustomizations.ConfigureXrdp
                    || vmCustomizations.ConfigureHtbVpn
                    || vmCustomizations.SyncTimezone;

                if (needsIsoBoot)
                {
                    progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachBootDvd" });
                    await _hyperVManager.AddBootDvd(vmSettings, cloningIsoPath, cancellationToken);
                    progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_SetBootOrder" });
                    await _hyperVManager.SetFirstBootToDvd(vmSettings, cancellationToken);
                }
                else
                {
                    progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_SetBootOrder" });
                    await _hyperVManager.SetFirstBootToHardDrive(vmSettings, cancellationToken);
                }
            }
            else if (detectedGeneration == 1)
            {
                progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachDisk" });
                await _hyperVManager.AddNewHardDrive(vmSettings, _pathService.DefaultVhdxPath, cancellationToken);

                progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachCloneDisk" });
                await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);
                _logger.LogInformation("Attached MBR disk as secondary for cloning: {MediaPath}", mediaPath);

                progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachBootDvd" });
                await _hyperVManager.AddBootDvd(vmSettings, cloningIsoPath, cancellationToken);

                progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_SetBootOrder" });
                await _hyperVManager.SetFirstBootToDvd(vmSettings, cancellationToken);
            }
            else
            {
                throw new Exception($"Unsupported generation detected: {detectedGeneration}");
            }

            if (vmSettings.VirtualizationEnabled)
            {
                progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_EnableNestedVirt" });
                await _hyperVManager.EnableVirtualization(vmSettings, cancellationToken);
            }
            else
            {
                _logger.LogInformation("Virtualization extensions not enabled for VM: {VMName}", vmSettings.VMName);
            }

            progress.Report(new CreateVMProgressInfo { Phase = "StartVM" });
            await _hyperVManager.StartVM(vmSettings, cancellationToken);

            bool needsPostBoot = GetPostBootSteps(item, vmCustomizations).Any();
            bool needsIsoBootCycle = detectedGeneration == 1
                || (detectedGeneration == 2 && (vmCustomizations.ConfigureXrdp || needsPostBoot));

            if (needsIsoBootCycle)
            {
                if (detectedGeneration == 2)
                {
                    progress.Report(new CreateVMProgressInfo
                    {
                        Phase = "Customize",
                        DetectedGeneration = "2"
                    });
                }

                // KVP corruption mitigation — see original implementation for full rationale.
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "PADDING_1", "true", cancellationToken);
                await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "PADDING_2", "true", cancellationToken);
                await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "PADDING_3", "true", cancellationToken);

                string sshPublicKey;
                if (!string.IsNullOrEmpty(vmCustomizations.CustomSshPublicKeyPath))
                    sshPublicKey = await _sshKeyManager.ReadPublicKeyAsync(vmCustomizations.CustomSshPublicKeyPath, cancellationToken);
                else
                    sshPublicKey = await _sshKeyManager.EnsureKeyPairAsync(cancellationToken);

                _logger.LogInformation("Sending SSH public key ({Length} chars) via KVP to VM {VMName}",
                    sshPublicKey?.Length ?? 0, vmSettings.VMName);
                await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_SSH_PUBKEY", sshPublicKey, cancellationToken);

                if (detectedGeneration == 2)
                    await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_MODE", "customize", cancellationToken);

                if (vmCustomizations.ConfigureXrdp)
                    await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_XRDP", "true", cancellationToken);

                string nameservers = vmCustomizations.DnsMode switch
                {
                    DnsMode.Custom => vmCustomizations.CustomNameservers,
                    _ => _hostNetworkService.ResolveHostDnsServers(),
                };
                if (!string.IsNullOrWhiteSpace(nameservers))
                {
                    _logger.LogInformation("Sending DNS nameservers via KVP to VM {VMName}: {Nameservers}",
                        vmSettings.VMName, nameservers);
                    await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_NAMESERVERS", nameservers, cancellationToken);
                }

                const int shutdownTimeoutSeconds = 600;
                bool shutDown;

                if (detectedGeneration == 1)
                {
                    bool cloneMarkerSeen = await _kvpPoller.PollKVPForProgressAsync(
                        vmSettings.VMName, progress, cancellationToken, shutdownTimeoutSeconds);

                    progress.Report(new CreateVMProgressInfo { Phase = "Customize" });

                    if (cloneMarkerSeen)
                    {
                        shutDown = await _kvpPoller.WaitForShutdownWithProgressAsync(
                            vmSettings.VMName, progress, cancellationToken, shutdownTimeoutSeconds);
                    }
                    else
                    {
                        shutDown = await _shutdownWatcher.WaitForVMShutdownAsync(vmSettings.VMName, cancellationToken, timeoutSeconds: 1);
                    }
                }
                else
                {
                    shutDown = await _kvpPoller.WaitForShutdownWithProgressAsync(
                        vmSettings.VMName, progress, cancellationToken, shutdownTimeoutSeconds);
                }

                if (!shutDown)
                {
                    _logger.LogWarning("VM {VMName} did not shut down within {Timeout}s — collecting diagnostics.", vmSettings.VMName, shutdownTimeoutSeconds);

                    var diagnostics = await _diagnosticsCollector
                        .CollectAsync(vmSettings.VMName, cancellationToken,
                            _sshKeyManager.GetPrivateKeyPath(vmCustomizations.CustomSshPublicKeyPath));

                    _logger.LogError("Guest diagnostics for {VMName}: {Summary}\n{RawOutput}",
                        vmSettings.VMName, diagnostics.Summary, diagnostics.RawOutput);

                    await _hyperVManager.StopVMAsync(vmSettings.VMName, cancellationToken);
                    _logger.LogInformation("Force-stopped VM {VMName} after timeout.", vmSettings.VMName);

                    progress.Report(new CreateVMProgressInfo
                    {
                        Phase = "Customize",
                        ErrorMessage = diagnostics.Summary,
                        DiagnosticsLog = diagnostics.RawOutput
                    });

                    throw new Exception($"ISO customization timed out. {diagnostics.Summary}");
                }

                if (detectedGeneration == 1)
                {
                    progress.Report(new CreateVMProgressInfo { Phase = "Customize", SubStep = "Sub_CleanupIsoBoot" });
                    await _hyperVManager.RemoveHardDrive(vmSettings, OriginalDiskScsiControllerLocation, cancellationToken);

                    if (File.Exists(mediaPath))
                    {
                        File.Delete(mediaPath);
                        _logger.LogInformation("Deleted original MBR source disk: {MediaPath}", mediaPath);
                    }
                }

                progress.Report(new CreateVMProgressInfo { Phase = "Customize", SubStep = "Sub_CleanupIsoBoot" });
                await _hyperVManager.RemoveBootDvd(vmSettings, cloningIsoPath, cancellationToken);
                await _hyperVManager.SetFirstBootToHardDrive(vmSettings, cancellationToken);
            }

            if (vmCustomizations.EnableIntegrationServices)
                await _hyperVManager.SetEnhancedSession(vmSettings, cancellationToken);

            var postBootSteps = GetPostBootSteps(item, vmCustomizations).ToList();

            if (needsIsoBootCycle)
            {
                if (postBootSteps.Count > 0)
                    progress.Report(new CreateVMProgressInfo { Phase = "PostBoot" });

                progress.Report(new CreateVMProgressInfo { Phase = "PostBoot", SubStep = "Sub_AddTempNic" });
                await _hyperVManager.RemoveTemporaryNetworkAdapter(vmSettings, cancellationToken);
                await _hyperVManager.AddTemporaryNetworkAdapter(vmSettings, cancellationToken);

                try
                {
                    await _hyperVManager.StartVM(vmSettings, cancellationToken);

                    string privateKeyPath = _sshKeyManager.GetPrivateKeyPath(vmCustomizations.CustomSshPublicKeyPath);
                    var shell = _guestShellFactory.Create(vmSettings.VMName, privateKeyPath);

                    progress.Report(new CreateVMProgressInfo { Phase = "PostBoot", SubStep = "Sub_WaitForSsh" });
                    await shell.WaitForReadyAsync(cancellationToken);

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

                    if (postBootSteps.Count > 0)
                    {
                        await _postBootService.RunLinuxPostBootAsync(
                            shell, vmSettings, item, vmCustomizations, progress, cancellationToken);
                    }
                }
                finally
                {
                    try
                    {
                        await _hyperVManager.RemoveTemporaryNetworkAdapter(vmSettings, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to remove temporary network adapter (non-fatal)");
                    }
                }
            }
        }

        private System.Collections.Generic.IEnumerable<ICustomizationStep> GetPostBootSteps(GalleryItem item, VmCustomizations customizations)
        {
            // This is a lightweight local helper to decide whether the ISO boot cycle is needed.
            // The actual execution and ordering is handled by PostBootCustomizationService.
            return _postBootService.HasLinuxPostBootSteps(item, customizations)
                ? new[] { (ICustomizationStep)null }
                : Array.Empty<ICustomizationStep>();
        }
    }
}
