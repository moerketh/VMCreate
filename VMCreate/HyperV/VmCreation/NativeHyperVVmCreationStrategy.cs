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
    internal class NativeHyperVVmCreationStrategy : IVmCreationStrategy
    {
        private readonly IHyperVManager _hyperVManager;
        private readonly IGuestShellFactory _guestShellFactory;
        private readonly IUnattendInjector _unattendInjector;
        private readonly IPostBootCustomizationService _postBootService;
        private readonly ILogger<NativeHyperVVmCreationStrategy> _logger;
        private readonly IVmPathService _pathService;

        public NativeHyperVVmCreationStrategy(
            IHyperVManager hyperVManager,
            IGuestShellFactory guestShellFactory,
            IUnattendInjector unattendInjector,
            IPostBootCustomizationService postBootService,
            ILogger<NativeHyperVVmCreationStrategy> logger,
            IVmPathService pathService)
        {
            _hyperVManager = hyperVManager ?? throw new ArgumentNullException(nameof(hyperVManager));
            _guestShellFactory = guestShellFactory ?? throw new ArgumentNullException(nameof(guestShellFactory));
            _unattendInjector = unattendInjector ?? throw new ArgumentNullException(nameof(unattendInjector));
            _postBootService = postBootService ?? throw new ArgumentNullException(nameof(postBootService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
        }

        public bool CanHandle(GalleryItem item, string actualFileType)
            => item.IsNativeHyperV;

        public async Task CreateVMAsync(VmCreationContext ctx)
        {
            VmSettings vmSettings = ctx.Settings;
            VmCustomizations vmCustomizations = ctx.Customizations;
            GalleryItem item = ctx.GalleryItem;
            string mediaPath = ctx.MediaPath;
            var cancellationToken = ctx.CancellationToken;
            var progress = ctx.Progress;

            const int targetGeneration = 2;

            if (!string.IsNullOrEmpty(item.SecureBootTemplate))
                vmSettings.SecureBootTemplate = item.SecureBootTemplate;
            else if (!string.Equals(item.SecureBoot, "false", StringComparison.OrdinalIgnoreCase))
                vmSettings.SecureBootTemplate = "MicrosoftWindows";

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_CreateVMSkeleton" });
            await _hyperVManager.CreateVMAsync(vmSettings, _pathService.DefaultVmPath, targetGeneration, cancellationToken);
            await _hyperVManager.SetVMLoginNotes(vmSettings, item.InitialUsername, item.InitialPassword, cancellationToken);

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConnectNic" });
            await _hyperVManager.ConnectNetworkAdapter(vmSettings, cancellationToken);

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConfigureHardware" });
            await _hyperVManager.SetCpuCount(vmSettings, cancellationToken);
            await _hyperVManager.DisableDynamicMemory(vmSettings, cancellationToken);
            await _hyperVManager.SetSecureBoot(vmSettings, cancellationToken);
            if (vmCustomizations.EnableIntegrationServices)
                await _hyperVManager.EnableGuestServices(vmSettings, cancellationToken);

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachDisk" });

            if (item.IsWindows)
            {
                var vhdxInfo = new FileInfo(mediaPath);
                if (vhdxInfo.IsReadOnly)
                {
                    _logger.LogInformation("Clearing read-only attribute on VHDX for VM {VMName}: {VhdxPath}", vmSettings.VMName, mediaPath);
                    vhdxInfo.IsReadOnly = false;
                }

                bool injected = await _unattendInjector.InjectAsync(mediaPath, cancellationToken);
                if (!injected)
                    throw new InvalidOperationException(
                        "Administrator approval is required to prepare the Windows image (unattend injection). Deployment cancelled.");
            }

            await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_SetBootOrder" });
            await _hyperVManager.SetFirstBootToHardDrive(vmSettings, cancellationToken);

            if (vmSettings.VirtualizationEnabled)
            {
                progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_EnableNestedVirt" });
                await _hyperVManager.EnableVirtualization(vmSettings, cancellationToken);
            }

            progress.Report(new CreateVMProgressInfo { Phase = "StartVM" });
            await _hyperVManager.StartVM(vmSettings, cancellationToken);

            if (vmCustomizations.EnableIntegrationServices)
                await _hyperVManager.SetEnhancedSession(vmSettings, cancellationToken);

            if (item.IsWindows)
                await RunWindowsPostBootAsync(ctx);
        }

        private async Task RunWindowsPostBootAsync(VmCreationContext ctx)
        {
            var shell = _guestShellFactory.CreateForWindows(
                ctx.Settings.VMName,
                ctx.GalleryItem.InitialUsername ?? "flare",
                ctx.GalleryItem.InitialPassword ?? "flare");

            ctx.Progress.Report(new CreateVMProgressInfo { Phase = "PostBoot", SubStep = "Sub_WaitForSsh" });
            await shell.WaitForReadyAsync(ctx.CancellationToken);

            await _postBootService.RunWindowsPostBootAsync(
                shell, ctx.Settings, ctx.GalleryItem, ctx.Customizations, ctx.Progress, ctx.CancellationToken);
        }
    }
}
