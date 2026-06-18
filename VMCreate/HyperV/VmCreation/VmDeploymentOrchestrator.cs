using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VMCreate.MediaHandlers;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Coordinates the full VM deployment flow: media preparation, strategy selection,
    /// parallel ISO provisioning, and cleanup on failure. Keeps <see cref="HyperVVmCreator"/>
    /// as a thin dispatcher around this orchestrator.
    /// </summary>
    public sealed class VmDeploymentOrchestrator : IVmDeploymentOrchestrator
    {
        private readonly IVmPathService _pathService;
        private readonly IMediaHandlerFactory _mediaHandlerFactory;
        private readonly IHyperVManager _hyperVManager;
        private readonly ILogger<VmDeploymentOrchestrator> _logger;
        private readonly ICloningIsoDownloader _cloningIsoDownloader;
        private readonly IEnumerable<IVmCreationStrategy> _strategies;

        public VmDeploymentOrchestrator(
            ILogger<VmDeploymentOrchestrator> logger,
            IMediaHandlerFactory mediaHandlerFactory,
            IHyperVManager hyperVManager,
            ICloningIsoDownloader cloningIsoDownloader,
            IEnumerable<IVmCreationStrategy> strategies,
            IVmPathService pathService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mediaHandlerFactory = mediaHandlerFactory ?? throw new ArgumentNullException(nameof(mediaHandlerFactory));
            _hyperVManager = hyperVManager ?? throw new ArgumentNullException(nameof(hyperVManager));
            _cloningIsoDownloader = cloningIsoDownloader ?? throw new ArgumentNullException(nameof(cloningIsoDownloader));
            _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
            _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
        }

        public async Task<VmDeploymentResult> DeployAsync(
            VmDeploymentPlan plan,
            VmCustomizations customizations,
            GalleryItem galleryItem,
            CancellationToken cancellationToken,
            IProgress<CreateVMProgressInfo> progress,
            string sourceFile)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (customizations == null) throw new ArgumentNullException(nameof(customizations));
            if (galleryItem == null) throw new ArgumentNullException(nameof(galleryItem));
            cancellationToken.ThrowIfCancellationRequested();

            string sourceFileOrUri = string.IsNullOrWhiteSpace(sourceFile)
                ? galleryItem.DiskUri
                : sourceFile;

            if (string.IsNullOrWhiteSpace(sourceFileOrUri))
                throw new ArgumentException("A source file or gallery disk URI must be provided.", nameof(sourceFile));

            bool vmCreated = false;
            IDeploymentLogger deploymentLogger = new DeploymentLogger(plan.VmName);
            try
            {
                _logger.LogInformation("Starting VM deployment for {VMName}", plan.VmName);

                cancellationToken.ThrowIfCancellationRequested();
                if (plan.ReplacePreviousVm)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ReplacePreviousVmAsync(plan, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();

                IProgress<CreateVMProgressInfo> effectiveProgress = progress ?? new Progress<CreateVMProgressInfo>(_ => { });

                IMediaHandler mediaHandler = _mediaHandlerFactory.CreateHandler(DiskFileDetector.DetectFileType(sourceFileOrUri));
                DiskImageFormat actualFileType = mediaHandler.FileType;

                Task isoProvisioningTask = EnsureCloningIsoAsync(plan, cancellationToken, effectiveProgress);

                cancellationToken.ThrowIfCancellationRequested();
                MediaPreparationResult mediaResult = await mediaHandler.PrepareMediaAsync(
                    sourceFileOrUri,
                    _pathService.GetVirtualHardDiskPath(plan.VmName),
                    plan,
                    galleryItem,
                    effectiveProgress,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                IVmCreationStrategy strategy = _strategies
                    .FirstOrDefault(s => s.CanHandle(galleryItem, actualFileType))
                    ?? throw new Exception($"No creation strategy found for gallery item '{galleryItem.Name}' with file type '{actualFileType}'.");

                if (isoProvisioningTask != null)
                    await isoProvisioningTask;

                cancellationToken.ThrowIfCancellationRequested();
                var strategyContext = new VmCreationContext(
                    plan,
                    customizations,
                    sourceFileOrUri,
                    galleryItem,
                    mediaResult,
                    cancellationToken,
                    effectiveProgress,
                    deploymentLogger);

                await strategy.CreateVMAsync(strategyContext);
                vmCreated = true;

                return new VmDeploymentResult(
                    plan.VmName,
                    success: true,
                    vmPath: Path.Combine(_pathService.DefaultVmPath, plan.VmName),
                    vhdxPath: mediaResult?.FinalMediaPath,
                    deploymentLog: deploymentLogger.GetLog());
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("VM deployment cancelled for {VMName} — cleaning up", plan.VmName);
                await CleanupFailedVmAsync(plan, vmCreated, preserveInDebug: System.Diagnostics.Debugger.IsAttached);
                return new VmDeploymentResult(plan.VmName, success: false, errorMessage: "Deployment cancelled.", deploymentLog: deploymentLogger.GetLog());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deploying VM: {Message}", ex.Message);
                await CleanupFailedVmAsync(plan, vmCreated, preserveInDebug: System.Diagnostics.Debugger.IsAttached);
                return new VmDeploymentResult(plan.VmName, success: false, errorMessage: ex.Message, deploymentLog: deploymentLogger.GetLog());
            }
        }

        private async Task EnsureCloningIsoAsync(
            VmDeploymentPlan plan,
            CancellationToken cancellationToken,
            IProgress<CreateVMProgressInfo> progress)
        {
            await _cloningIsoDownloader.EnsureIsoAsync(plan, cancellationToken, progress);
        }

        private async Task CleanupFailedVmAsync(VmDeploymentPlan plan, bool vmCreated, bool preserveInDebug)
        {
            if (preserveInDebug)
            {
                _logger.LogWarning("Debug mode: skipping cleanup for {VMName} — VM preserved for investigation", plan.VmName);
                return;
            }

            string vmName = plan.VmName;
            _logger.LogInformation("Cleaning up after failed/cancelled deployment: {VMName}", vmName);
            try
            {
                if (vmCreated)
                {
                    string[] vhdxPaths;
                    try
                    {
                        vhdxPaths = await _hyperVManager.GetVmHardDiskPathsAsync(vmName, CancellationToken.None);
                    }
                    catch
                    {
                        vhdxPaths = Array.Empty<string>();
                    }

                    try { await _hyperVManager.StopVMAsync(vmName, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Stop VM during cleanup (may not be running)"); }

                    try { await _hyperVManager.RemoveVMAsync(vmName, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove VM {VMName} during cleanup", vmName); }

                    foreach (string vhdxPath in vhdxPaths)
                    {
                        TryDismountVhdx(vhdxPath);
                    }

                    foreach (string vhdxPath in vhdxPaths)
                    {
                        try
                        {
                            if (File.Exists(vhdxPath))
                            {
                                File.Delete(vhdxPath);
                                _logger.LogInformation("Cleanup: deleted VHDX {Path}", vhdxPath);
                            }
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "Cleanup: failed to delete {Path}", vhdxPath); }
                    }
                }

                string convertedVhdx = Path.Combine(_pathService.DefaultVhdxPath, vmName + ".vhdx");
                TryDismountVhdx(convertedVhdx);
                try
                {
                    if (File.Exists(convertedVhdx))
                    {
                        File.Delete(convertedVhdx);
                        _logger.LogInformation("Cleanup: deleted converted VHDX {Path}", convertedVhdx);
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Cleanup: failed to delete {Path}", convertedVhdx); }

                string vmConfigFolder = Path.Combine(_pathService.DefaultVmPath, vmName);
                try
                {
                    if (Directory.Exists(vmConfigFolder))
                    {
                        Directory.Delete(vmConfigFolder, true);
                        _logger.LogInformation("Cleanup: deleted VM config folder {Path}", vmConfigFolder);
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Cleanup: failed to delete folder {Path}", vmConfigFolder); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup encountered an unexpected error for {VMName}", vmName);
            }
        }

        private void TryDismountVhdx(string vhdxPath)
        {
            try
            {
                using var ps = System.Management.Automation.PowerShell.Create();
                ps.AddCommand("Import-Module").AddParameter("Name", "Hyper-V").Invoke();
                ps.Commands.Clear();
                ps.AddCommand("Dismount-VHD").AddParameter("Path", vhdxPath);
                ps.Invoke();
                if (ps.HadErrors)
                    _logger.LogDebug("Dismount-VHD reported errors (VHDX may not have been mounted): {Error}",
                        string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
                else
                    _logger.LogInformation("Dismounted VHDX during cleanup: {VhdxPath}", vhdxPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dismount-VHD threw (VHDX may not have been mounted)");
            }
        }

        private async Task ReplacePreviousVmAsync(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            string fullName = plan.VmName;
            string baseName = fullName;
            int lastUnderscore = fullName.LastIndexOf('_');
            if (lastUnderscore > 0 && fullName.Length - lastUnderscore - 1 == 14)
            {
                baseName = fullName.Substring(0, lastUnderscore);
            }

            _logger.LogInformation("Looking for existing VMs matching base name: {BaseName}", baseName);
            string[] existingVms = await _hyperVManager.FindExistingVmsByBaseNameAsync(baseName, cancellationToken);

            foreach (var existingVmName in existingVms)
            {
                if (string.Equals(existingVmName, fullName, StringComparison.OrdinalIgnoreCase))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Replacing existing VM: {ExistingVMName}", existingVmName);

                string[] vhdxPaths = await _hyperVManager.GetVmHardDiskPathsAsync(existingVmName, cancellationToken);

                await _hyperVManager.StopVMAsync(existingVmName, cancellationToken);
                await _hyperVManager.RemoveVMAsync(existingVmName, cancellationToken);

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

                string vmConfigFolder = Path.Combine(_pathService.DefaultVmPath, existingVmName);
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
