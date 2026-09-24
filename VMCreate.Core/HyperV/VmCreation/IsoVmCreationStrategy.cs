using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.Cidata;
using VMCreate.MediaHandlers;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Creates a VM from an ISO installer image. Attaches an empty VHDX, mounts the ISO,
    /// and boots the VM so the installer can run. For Windows ISOs an unattend.xml ISO is
    /// also attached so setup runs unattended, followed by optional post-boot customization
    /// steps over PowerShell Direct. For Omarchy (Arch) ISOs a cidata seed ISO is attached
    /// so the installer runs fully unattended; deployment then waits for the installed
    /// system's SSH to come up.
    /// </summary>
    public class IsoVmCreationStrategy : IVmCreationStrategy
    {
        private readonly IVmLifecycleManager _lifecycleManager;
        private readonly IVmDiskManager _diskManager;
        private readonly IVmBootManager _bootManager;
        private readonly IVmNetworkManager _networkManager;
        private readonly IVmConfigManager _configManager;
        private readonly IGuestShellFactory _guestShellFactory;
        private readonly ISshKeyManager _sshKeyManager;
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
            ISshKeyManager sshKeyManager,
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
            _sshKeyManager = sshKeyManager ?? throw new ArgumentNullException(nameof(sshKeyManager));
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

            string? unattendIsoPath = null;
            string? cidataIsoPath = null;
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
                    await _bootManager.AttachIsoToNewDvdDrive(plan, unattendIsoPath, cancellationToken);
                    _logger.LogInformation("Attached unattend.xml ISO to VM {VMName}", plan.VmName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create unattend.xml ISO for VM {VMName} — Windows installation will require manual input", plan.VmName);
                    unattendIsoPath = null;
                }
            }
            else if (item.LinuxDistro == LinuxDistro.Arch)
            {
                _logger.LogInformation("Creating cidata seed ISO for unattended Omarchy installation on VM {VMName}", plan.VmName);
                cidataIsoPath = Path.Combine(_pathService.DefaultVhdxPath, $"{plan.VmName}_cidata.iso");
                // Validate the plan's drive size up front: a misconfigured plan
                // (zero, or below Omarchy's ~20 GB floor) cannot be fixed by the
                // warn-and-continue fallback below, which would just boot a live
                // ISO that fails at partition time.
                if (plan.NewDriveSizeInGB * 1024L * 1024 * 1024 < CidataSeedBuilder.MinimumDiskSizeBytes)
                    throw new ArgumentException(
                        $"Deployment plan drive size {plan.NewDriveSizeInGB} GB is below the Omarchy minimum of {CidataSeedBuilder.MinimumDiskSizeBytes / (1024L * 1024 * 1024)} GB.",
                        nameof(plan));
                try
                {
                    CidataSeedOptions seedOptions = await BuildCidataSeedOptionsAsync(plan, item, vmCustomizations, cancellationToken);
                    CidataSeedBuilder.BuildCidataIso(cidataIsoPath, seedOptions, _logger);
                    await _bootManager.AttachIsoToNewDvdDrive(plan, cidataIsoPath, cancellationToken);
                    _logger.LogInformation("Attached cidata seed ISO to VM {VMName} on a dedicated drive — installer ISO stays bootable", plan.VmName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create cidata seed ISO for VM {VMName} — falling back to DVD boot; Omarchy will require a fully manual install", plan.VmName);
                    cidataIsoPath = null;
                }
            }

            progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.CreateVM, VmDeploymentSubStep.SetBootOrder));
            if (cidataIsoPath != null)
            {
                // The target disk is still blank, so firmware falls through to
                // the installer DVD on the first boot. Omarchy's installer
                // reboots into the installed system by itself — with DVD-first
                // boot order that reboot would loop straight back into the
                // installer, so the hard drive must stay first.
                await _bootManager.SetFirstBootToHardDrive(plan, cancellationToken);
                await _configManager.SetVMLoginNotes(plan, item.InitialUsername, item.InitialPassword, cancellationToken);
            }
            else
            {
                await _bootManager.SetFirstBootToDvd(plan, cancellationToken);
            }

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

            if (cidataIsoPath != null)
                await WaitForOmarchyInstallAsync(ctx, cidataIsoPath);
            else if (unattendIsoPath != null && File.Exists(unattendIsoPath))
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

        /// <summary>
        /// Builds the seed options for an unattended Omarchy install. The installer
        /// only enables sshd on the installed system when the seed carries an
        /// authorized_keys file, so the deployment SSH key is always injected.
        /// </summary>
        private async Task<CidataSeedOptions> BuildCidataSeedOptionsAsync(
            VmDeploymentPlan plan, GalleryItem item, VmCustomizations customizations, CancellationToken ct)
        {
            string publicKey = customizations.CustomSshPublicKeyPath != null
                ? await _sshKeyManager.ReadPublicKeyAsync(customizations.CustomSshPublicKeyPath, ct)
                : await _sshKeyManager.EnsureKeyPairAsync(ct);

            return new CidataSeedOptions
            {
                Username = item.InitialUsername ?? "omarchy",
                Password = item.InitialPassword ?? "omarchy",
                Hostname = plan.VmName,
                AuthorizedKeys = publicKey,
                DiskSizeBytes = (long)plan.NewDriveSizeInGB * 1024 * 1024 * 1024,
            };
        }

        /// <summary>
        /// Waits for the unattended Omarchy install to finish, then detaches and
        /// deletes the seed ISO. The live ISO environment has no SSH server — sshd
        /// only comes up on the installed system (enabled because the seed carried
        /// authorized_keys), after the installer downloaded all packages and
        /// rebooted. That can far exceed a single WaitForReadyAsync window, so the
        /// wait is retried with fresh IP discovery each round. Generic Linux
        /// post-boot steps are intentionally not run: Omarchy's installer grants
        /// sudo only with a password, and every shared step shells out through
        /// non-interactive sudo.
        /// </summary>
        private async Task WaitForOmarchyInstallAsync(VmCreationContext ctx, string cidataIsoPath)
        {
            try
            {
                string privateKeyPath = _sshKeyManager.GetPrivateKeyPath(ctx.Customizations.CustomSshPublicKeyPath);
                string username = ctx.GalleryItem.InitialUsername ?? "omarchy";
                var shell = _guestShellFactory.Create(ctx.Plan.VmName, privateKeyPath, username);

                const int maxAttempts = 4; // 4 × 10 min covers a full cold-mirror package download on a reasonably fast link
                for (int attempt = 1; ; attempt++)
                {
                    ctx.Progress.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.PostBoot, VmDeploymentSubStep.WaitForSsh));
                    try
                    {
                        await shell.WaitForReadyAsync(ctx.CancellationToken);
                        _logger.LogInformation("Omarchy installation finished on VM {VMName} — installed system reachable over SSH", ctx.Plan.VmName);
                        break;
                    }
                    catch (TimeoutException) when (attempt < maxAttempts)
                    {
                        _logger.LogInformation(
                            "Installed system on VM {VMName} not reachable yet (attempt {Attempt}/{MaxAttempts}) — installer still working",
                            ctx.Plan.VmName, attempt, maxAttempts);
                    }
                }
            }
            finally
            {
                // The seed is consumed at live-boot start (files are copied off,
                // then the volume unmounted), so it can be detached any time
                // after the VM has booted once.
                try
                {
                    if (File.Exists(cidataIsoPath))
                    {
                        await _bootManager.RemoveBootDvd(ctx.Plan, cidataIsoPath, ctx.CancellationToken);
                        File.Delete(cidataIsoPath);
                        _logger.LogInformation("Cleaned up cidata seed ISO for VM {VMName}", ctx.Plan.VmName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up cidata seed ISO for VM {VMName}", ctx.Plan.VmName);
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
