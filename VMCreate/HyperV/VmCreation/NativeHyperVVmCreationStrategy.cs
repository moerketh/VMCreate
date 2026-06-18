using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;
using VMCreate.MediaHandlers;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Creates a VM from a native Hyper-V image (pre-built VHDX). These images require
    /// no conversion. For Windows native images, unattend.xml is injected into the VHDX
    /// via an elevated child process before first boot, then optional post-boot
    /// customization steps run over PowerShell Direct.
    /// </summary>
    public class NativeHyperVVmCreationStrategy : IVmCreationStrategy
    {
        private readonly IVmLifecycleManager _lifecycleManager;
        private readonly IVmDiskManager _diskManager;
        private readonly IVmBootManager _bootManager;
        private readonly IVmNetworkManager _networkManager;
        private readonly IVmConfigManager _configManager;
        private readonly IGuestShellFactory _guestShellFactory;
        private readonly IUnattendInjector _unattendInjector;
        private readonly IPostBootCustomizationService _postBootService;
        private readonly ILogger<NativeHyperVVmCreationStrategy> _logger;
        private readonly IVmPathService _pathService;

        public NativeHyperVVmCreationStrategy(
            IVmLifecycleManager lifecycleManager,
            IVmDiskManager diskManager,
            IVmBootManager bootManager,
            IVmNetworkManager networkManager,
            IVmConfigManager configManager,
            IGuestShellFactory guestShellFactory,
            IUnattendInjector unattendInjector,
            IPostBootCustomizationService postBootService,
            ILogger<NativeHyperVVmCreationStrategy> logger,
            IVmPathService pathService)
        {
            _lifecycleManager = lifecycleManager ?? throw new ArgumentNullException(nameof(lifecycleManager));
            _diskManager = diskManager ?? throw new ArgumentNullException(nameof(diskManager));
            _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
            _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _guestShellFactory = guestShellFactory ?? throw new ArgumentNullException(nameof(guestShellFactory));
            _unattendInjector = unattendInjector ?? throw new ArgumentNullException(nameof(unattendInjector));
            _postBootService = postBootService ?? throw new ArgumentNullException(nameof(postBootService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
        }

        public bool CanHandle(GalleryItem item, DiskImageFormat actualFileType)
            => item.IsNativeHyperV;

        public async Task CreateVMAsync(VmCreationContext ctx)
        {
            VmDeploymentPlan plan = ctx.Plan;
            VmCustomizations vmCustomizations = ctx.Customizations;
            GalleryItem item = ctx.GalleryItem;
            string mediaPath = ctx.MediaResult.FinalMediaPath;
            int detectedGeneration = ctx.MediaResult.VmGeneration;
            var cancellationToken = ctx.CancellationToken;
            var progress = ctx.Progress;

            string secureBootTemplate;
            if (!string.IsNullOrEmpty(item.SecureBootTemplate))
                secureBootTemplate = item.SecureBootTemplate;
            else if (!string.Equals(item.SecureBoot, "false", StringComparison.OrdinalIgnoreCase))
                secureBootTemplate = "MicrosoftWindows";
            else
                secureBootTemplate = plan.SecureBootTemplate;

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.CreateVMSkeleton));
            await _lifecycleManager.CreateVMAsync(plan, _pathService.DefaultVmPath, detectedGeneration, cancellationToken);
            await _configManager.SetVMLoginNotes(plan, item.InitialUsername, item.InitialPassword, cancellationToken);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.ConnectNic));
            await _networkManager.ConnectNetworkAdapter(plan, cancellationToken);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.ConfigureHardware));
            await _configManager.SetCpuCount(plan, cancellationToken);
            await _configManager.DisableDynamicMemory(plan, cancellationToken);
            await _bootManager.SetSecureBoot(plan, secureBootTemplate, cancellationToken);
            if (vmCustomizations.EnableIntegrationServices)
                await _configManager.EnableGuestServices(plan, cancellationToken);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachDisk));

            if (item.IsWindows)
            {
                var vhdxInfo = new FileInfo(mediaPath);
                if (vhdxInfo.IsReadOnly)
                {
                    _logger.LogInformation("Clearing read-only attribute on VHDX for VM {VMName}: {VhdxPath}", plan.VmName, mediaPath);
                    vhdxInfo.IsReadOnly = false;
                }

                bool injected = await _unattendInjector.InjectAsync(mediaPath, cancellationToken);
                if (!injected)
                    throw new InvalidOperationException(
                        "Administrator approval is required to prepare the Windows image (unattend injection). Deployment cancelled.");
            }

            await _diskManager.AddExistingHardDrive(plan, mediaPath, cancellationToken);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.SetBootOrder));
            await _bootManager.SetFirstBootToHardDrive(plan, cancellationToken);

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
