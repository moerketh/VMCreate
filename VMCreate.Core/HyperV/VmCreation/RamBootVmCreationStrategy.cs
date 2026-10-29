using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using VMCreate.MediaHandlers;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Creates a diskless VM that boots a live ISO entirely into guest RAM
    /// (Puppy-style RAM boot). No virtual hard disk is created or attached —
    /// the ISO is attached as the boot DVD, so the machine is stateless:
    /// powering it off discards every change made during the session.
    /// Skips unattend automation and post-boot customization, which assume a
    /// persistent OS installation.
    /// </summary>
    public class RamBootVmCreationStrategy : IVmCreationStrategy
    {
        private readonly IVmLifecycleManager _lifecycleManager;
        private readonly IVmBootManager _bootManager;
        private readonly IVmNetworkManager _networkManager;
        private readonly IVmConfigManager _configManager;
        private readonly ILogger<RamBootVmCreationStrategy> _logger;
        private readonly IVmPathService _pathService;

        public RamBootVmCreationStrategy(
            IVmLifecycleManager lifecycleManager,
            IVmBootManager bootManager,
            IVmNetworkManager networkManager,
            IVmConfigManager configManager,
            ILogger<RamBootVmCreationStrategy> logger,
            IVmPathService pathService)
        {
            _lifecycleManager = lifecycleManager ?? throw new ArgumentNullException(nameof(lifecycleManager));
            _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
            _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
        }

        public bool CanHandle(GalleryItem item, DiskImageFormat actualFileType)
            => item.IsDiskless && actualFileType == DiskImageFormat.Iso;

        public async Task CreateVMAsync(VmCreationContext ctx)
        {
            ctx.Logger.Log($"Starting RAM-boot (diskless) VM creation for {ctx.Plan.VmName}");

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

            _logger.LogInformation(
                "Skipping hard disk creation for RAM-boot VM {VMName} — it runs entirely in guest memory",
                plan.VmName);

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.AttachBootDvd));
            await _bootManager.AddBootDvd(plan, mediaPath, cancellationToken);

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
        }
    }
}
