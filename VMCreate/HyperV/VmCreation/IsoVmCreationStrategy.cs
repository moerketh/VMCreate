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
    internal class IsoVmCreationStrategy : IVmCreationStrategy
    {
        private readonly IHyperVManager _hyperVManager;
        private readonly IGuestShellFactory _guestShellFactory;
        private readonly IPostBootCustomizationService _postBootService;
        private readonly ILogger<IsoVmCreationStrategy> _logger;
        private readonly IVmPathService _pathService;

        public IsoVmCreationStrategy(
            IHyperVManager hyperVManager,
            IGuestShellFactory guestShellFactory,
            IPostBootCustomizationService postBootService,
            ILogger<IsoVmCreationStrategy> logger,
            IVmPathService pathService)
        {
            _hyperVManager = hyperVManager ?? throw new ArgumentNullException(nameof(hyperVManager));
            _guestShellFactory = guestShellFactory ?? throw new ArgumentNullException(nameof(guestShellFactory));
            _postBootService = postBootService ?? throw new ArgumentNullException(nameof(postBootService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
        }

        public bool CanHandle(GalleryItem item, string actualFileType)
            => string.Equals(actualFileType, "ISO", StringComparison.OrdinalIgnoreCase);

        public async Task CreateVMAsync(VmCreationContext ctx)
        {
            VmSettings vmSettings = ctx.Settings;
            VmCustomizations vmCustomizations = ctx.Customizations;
            GalleryItem item = ctx.GalleryItem;
            string mediaPath = ctx.SourceFile;
            var cancellationToken = ctx.CancellationToken;
            var progress = ctx.Progress;

            const int targetGeneration = 2;

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM" });

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_CreateVMSkeleton" });
            await _hyperVManager.CreateVMAsync(vmSettings, _pathService.DefaultVmPath, targetGeneration, cancellationToken);
            await _hyperVManager.SetVMLoginNotes(vmSettings, item.InitialUsername, item.InitialPassword, cancellationToken);

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConnectNic" });
            await _hyperVManager.ConnectNetworkAdapter(vmSettings, cancellationToken);

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConfigureHardware" });
            await _hyperVManager.SetCpuCount(vmSettings, cancellationToken);
            await _hyperVManager.DisableDynamicMemory(vmSettings, cancellationToken);

            if (!string.IsNullOrEmpty(item.SecureBootTemplate))
                vmSettings.SecureBootTemplate = item.SecureBootTemplate;
            await _hyperVManager.SetSecureBoot(vmSettings, cancellationToken);

            if (vmCustomizations.EnableIntegrationServices)
                await _hyperVManager.EnableGuestServices(vmSettings, cancellationToken);

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachDisk" });
            await _hyperVManager.AddNewHardDrive(vmSettings, _pathService.DefaultVhdxPath, cancellationToken);

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachBootDvd" });
            await _hyperVManager.AddBootDvd(vmSettings, mediaPath, cancellationToken);

            string unattendIsoPath = null;
            if (item.IsWindows)
            {
                _logger.LogInformation("Creating unattend.xml ISO for automated Windows installation on VM {VMName}", vmSettings.VMName);
                unattendIsoPath = Path.Combine(_pathService.DefaultVhdxPath, $"{vmSettings.VMName}_unattend.iso");
                try
                {
                    UnattendFloppyBuilder.BuildUnattendIsoFromContent(
                        unattendIsoPath,
                        File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Unattend", "autounattend.xml")),
                        _logger);
                    await _hyperVManager.AddBootDvd(vmSettings, unattendIsoPath, cancellationToken);
                    _logger.LogInformation("Attached unattend.xml ISO to VM {VMName}", vmSettings.VMName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create unattend.xml ISO for VM {VMName} — Windows installation will require manual input", vmSettings.VMName);
                    unattendIsoPath = null;
                }
            }

            progress.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_SetBootOrder" });
            await _hyperVManager.SetFirstBootToDvd(vmSettings, cancellationToken);

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

            if (unattendIsoPath != null && File.Exists(unattendIsoPath))
            {
                try
                {
                    await _hyperVManager.RemoveBootDvd(vmSettings, unattendIsoPath, cancellationToken);
                    File.Delete(unattendIsoPath);
                    _logger.LogInformation("Cleaned up unattend ISO for VM {VMName}", vmSettings.VMName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up unattend ISO for VM {VMName}", vmSettings.VMName);
                }
            }
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
