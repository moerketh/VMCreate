using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;
using VMCreate.MediaHandlers;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Creates a VM from an ISO installer image. Attaches an empty VHDX, mounts the ISO,
    /// and boots the VM so the installer can run. For Windows ISOs an unattend.xml ISO is
    /// also attached so setup runs unattended, followed by optional post-boot customization
    /// steps over PowerShell Direct.
    /// </summary>
    public class IsoVmCreationStrategy : IVmCreationStrategy
    {
        private readonly IVmLifecycleManager _lifecycleManager;
        private readonly IVmDiskManager _diskManager;
        private readonly IVmBootManager _bootManager;
        private readonly IVmNetworkManager _networkManager;
        private readonly IVmConfigManager _configManager;
        private readonly IGuestShellFactory _guestShellFactory;
        private readonly IPostBootCustomizationService _postBootService;
        private readonly ILogger<IsoVmCreationStrategy> _logger;
        private readonly IVmPathService _pathService;

        public IsoVmCreationStrategy(
            IVmLifecycleManager lifecycleManager,
            IVmDiskManager diskManager,
            IVmBootManager bootManager,
            IVmNetworkManager networkManager,
            IVmConfigManager configManager,
            IGuestShellFactory guestShellFactory,
            IPostBootCustomizationService postBootService,
            ILogger<IsoVmCreationStrategy> logger,
            IVmPathService pathService)
        {
            _lifecycleManager = lifecycleManager ?? throw new ArgumentNullException(nameof(lifecycleManager));
            _diskManager = diskManager ?? throw new ArgumentNullException(nameof(diskManager));
            _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
            _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _guestShellFactory = guestShellFactory ?? throw new ArgumentNullException(nameof(guestShellFactory));
            _postBootService = postBootService ?? throw new ArgumentNullException(nameof(postBootService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
        }

        public bool CanHandle(GalleryItem item, DiskImageFormat actualFileType)
            => actualFileType == DiskImageFormat.Iso;

        public async Task CreateVMAsync(VmCreationContext ctx)
        {
            ctx.Logger.Log($"Starting ISO VM creation for {ctx.Plan.VmName}");

            VmDeploymentPlan plan = ctx.Plan;
            VmCustomizations vmCustomizations = ctx.Customizations;
            GalleryItem item = ctx.GalleryItem;
            string mediaPath = ctx.MediaResult.FinalMediaPath;
            int detectedGeneration = ctx.MediaResult.VmGeneration;
            var cancellationToken = ctx.CancellationToken;
            var progress = ctx.Progress;

            string defaultVmPath = _pathService.DefaultVmPath;

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.CreateVMSkeleton));
            await _lifecycleManager.CreateVMAsync(plan, defaultVmPath, detectedGeneration, cancellationToken);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.ConnectNic));
            await _networkManager.ConnectNetworkAdapter(plan, cancellationToken);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.ConfigureHardware));
            await _configManager.SetCpuCount(plan, cancellationToken);
            await _configManager.DisableDynamicMemory(plan, cancellationToken);

            string secureBootTemplate = item.SecureBootTemplate ?? plan.SecureBootTemplate;
            await _bootManager.SetSecureBoot(plan, secureBootTemplate, cancellationToken);

            if (vmCustomizations.EnableIntegrationServices)
                await _configManager.EnableGuestServices(plan, cancellationToken);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachDisk));
            await _diskManager.AddNewHardDrive(plan, _pathService.DefaultVhdxPath, cancellationToken);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachBootDvd));
            await _bootManager.AddBootDvd(plan, mediaPath, cancellationToken);

            string unattendIsoPath = null;
            if (item.IsWindows)
            {
                _logger.LogInformation("Creating unattend.xml ISO for automated Windows installation on VM {VMName}", plan.VmName);
                unattendIsoPath = Path.Combine(_pathService.DefaultVhdxPath, $"{plan.VmName}_unattend.iso");
                try
                {
                    UnattendFloppyBuilder.BuildUnattendIsoFromContent(
                        unattendIsoPath,
                        File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Unattend", "autounattend.xml")),
                        _logger);
                    await _bootManager.AddBootDvd(plan, unattendIsoPath, cancellationToken);
                    _logger.LogInformation("Attached unattend.xml ISO to VM {VMName}", plan.VmName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create unattend.xml ISO for VM {VMName} — Windows installation will require manual input", plan.VmName);
                    unattendIsoPath = null;
                }
            }

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.SetBootOrder));
            await _bootManager.SetFirstBootToDvd(plan, cancellationToken);

            if (plan.VirtualizationEnabled)
            {
                progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.EnableNestedVirt));
                await _configManager.EnableVirtualization(plan, cancellationToken);
            }

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.StartVM));
            await _lifecycleManager.StartVM(plan, cancellationToken);

            if (vmCustomizations.EnableIntegrationServices)
                await _configManager.SetEnhancedSession(plan, cancellationToken);

            if (item.IsWindows)
                await RunWindowsPostBootAsync(ctx);

            if (unattendIsoPath != null && File.Exists(unattendIsoPath))
            {
                try
                {
                    await _bootManager.RemoveBootDvd(plan, unattendIsoPath, cancellationToken);
                    File.Delete(unattendIsoPath);
                    _logger.LogInformation("Cleaned up unattend ISO for VM {VMName}", plan.VmName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up unattend ISO for VM {VMName}", plan.VmName);
                }
            }
        }

        private async Task RunWindowsPostBootAsync(VmCreationContext ctx)
        {
            var shell = _guestShellFactory.CreateForWindows(
                ctx.Plan.VmName,
                ctx.GalleryItem.InitialUsername ?? "flare",
                ctx.GalleryItem.InitialPassword ?? "flare");

            ctx.Progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.PostBoot, VmDeploymentSubStep.WaitForSsh));
            await shell.WaitForReadyAsync(ctx.CancellationToken);

            await _postBootService.RunWindowsPostBootAsync(
                shell, ctx.Plan, ctx.GalleryItem, ctx.Customizations, ctx.Progress, ctx.CancellationToken);
        }
    }
}
