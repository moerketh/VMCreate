using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
    public class DiskImageVmCreationStrategy : IVmCreationStrategy
    {
        private readonly IVmLifecycleManager _lifecycleManager;
        private readonly IVmDiskManager _diskManager;
        private readonly IVmBootManager _bootManager;
        private readonly IVmNetworkManager _networkManager;
        private readonly IVmConfigManager _configManager;
        private readonly IGuestShellFactory _guestShellFactory;
        private readonly ISshKeyManager _sshKeyManager;
        private readonly IKvpSender _kvpSender;
        private readonly IKvpPoller _kvpPoller;
        private readonly IVmShutdownWatcher _shutdownWatcher;
        private readonly IGuestDiagnosticsCollector _diagnosticsCollector;
        private readonly IPostBootCustomizationService _postBootService;
        private readonly IHostNetworkService _hostNetworkService;
        private readonly IVmPathService _pathService;
        private readonly IIsoBootCycleRunner _isoBootRunner;
        private readonly ILogger<DiskImageVmCreationStrategy> _logger;
        private const int OriginalDiskScsiControllerLocation = 1;

        public DiskImageVmCreationStrategy(
            IVmLifecycleManager lifecycleManager,
            IVmDiskManager diskManager,
            IVmBootManager bootManager,
            IVmNetworkManager networkManager,
            IVmConfigManager configManager,
            IGuestShellFactory guestShellFactory,
            ISshKeyManager sshKeyManager,
            IKvpSender kvpSender,
            IKvpPoller kvpPoller,
            IVmShutdownWatcher shutdownWatcher,
            IGuestDiagnosticsCollector diagnosticsCollector,
            IPostBootCustomizationService postBootService,
            IHostNetworkService hostNetworkService,
            ILogger<DiskImageVmCreationStrategy> logger,
            IVmPathService pathService,
            IIsoBootCycleRunner isoBootRunner)
        {
            _lifecycleManager = lifecycleManager ?? throw new ArgumentNullException(nameof(lifecycleManager));
            _diskManager = diskManager ?? throw new ArgumentNullException(nameof(diskManager));
            _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
            _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
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
            _isoBootRunner = isoBootRunner ?? throw new ArgumentNullException(nameof(isoBootRunner));
        }

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

            int detectedGeneration = ctx.MediaResult.VmGeneration;
            const int targetGen = 2;

            ctx.Logger.Log($"Detected generation {detectedGeneration}, targeting Gen {targetGen}");

            progress.Report(new CreateVMProgressInfo { Phase = VmDeploymentPhase.CreateVM, DetectedGeneration = detectedGeneration });

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

                bool needsIsoBoot = vmCustomizations.ConfigureXrdp
                    || vmCustomizations.SyncTimezone
                    || _postBootService.HasLinuxPostBootSteps(item, vmCustomizations);

                if (needsIsoBoot)
                {
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
            else if (detectedGeneration == 1)
            {
                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachDisk));
                await _diskManager.AddNewHardDrive(plan, _pathService.DefaultVhdxPath, cancellationToken);

                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachCloneDisk));
                await _diskManager.AddExistingHardDrive(plan, mediaPath, cancellationToken);
                _logger.LogInformation("Attached MBR disk as secondary for cloning: {MediaPath}", mediaPath);

                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachBootDvd));
                await _bootManager.AddBootDvd(plan, cloningIsoPath, cancellationToken);

                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.SetBootOrder));
                await _bootManager.SetFirstBootToDvd(plan, cancellationToken);
            }
            else
            {
                throw new Exception($"Unsupported generation detected: {detectedGeneration}");
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

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.StartVM));
            await _lifecycleManager.StartVM(plan, cancellationToken);

            bool needsPostBoot = GetPostBootSteps(item, vmCustomizations).Any();
            bool needsIsoBootCycle = detectedGeneration == 1
                || (detectedGeneration == 2 && (vmCustomizations.ConfigureXrdp || needsPostBoot));

            if (needsIsoBootCycle)
            {
                var isoResult = await _isoBootRunner.RunAsync(
                    ctx, detectedGeneration, mediaPath, vmCustomizations, progress, cancellationToken);

                if (!isoResult.Success)
                {
                    throw new Exception($"ISO customization failed. {isoResult.ErrorMessage}");
                }
            }

            if (vmCustomizations.EnableIntegrationServices)
                await _configManager.SetEnhancedSession(plan, cancellationToken);

            var postBootSteps = GetPostBootSteps(item, vmCustomizations).ToList();

            if (needsIsoBootCycle)
            {
                if (postBootSteps.Count > 0)
                    progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.PostBoot));

                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.PostBoot, VmDeploymentSubStep.AddTempNic));
                await _networkManager.RemoveTemporaryNetworkAdapter(plan, cancellationToken);
                await _networkManager.AddTemporaryNetworkAdapter(plan, cancellationToken);

                try
                {
                    await _lifecycleManager.StartVM(plan, cancellationToken);

                    string privateKeyPath = _sshKeyManager.GetPrivateKeyPath(vmCustomizations.CustomSshPublicKeyPath);
                    var shell = _guestShellFactory.Create(plan.VmName, privateKeyPath);

                    progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.PostBoot, VmDeploymentSubStep.WaitForSsh));
                    await shell.WaitForReadyAsync(cancellationToken);

                    try
                    {
                        string autorunLog = await shell.RunCommandAsync(
                            "sudo cat /var/log/vmcreate-autorun.log 2>/dev/null || echo '[no autorun log found]'",
                            cancellationToken);
                        _logger.LogInformation("Autorun log for {VMName}:\n{Log}", plan.VmName, autorunLog);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to collect autorun log (non-fatal)");
                    }

                    if (postBootSteps.Count > 0)
                    {
                        await _postBootService.RunLinuxPostBootAsync(
                            shell, plan, item, vmCustomizations, progress, cancellationToken);
                    }
                }
                finally
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
            }
        }

        private IEnumerable<ICustomizationStep> GetPostBootSteps(GalleryItem item, VmCustomizations customizations)
        {
            return _postBootService.HasLinuxPostBootSteps(item, customizations)
                ? new[] { (ICustomizationStep)null }
                : Array.Empty<ICustomizationStep>();
        }
    }
}
