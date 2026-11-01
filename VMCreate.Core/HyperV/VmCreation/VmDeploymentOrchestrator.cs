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
        private readonly IVmMetadataRecorder? _metadataRecorder;

        public VmDeploymentOrchestrator(
            ILogger<VmDeploymentOrchestrator> logger,
            IMediaHandlerFactory mediaHandlerFactory,
            IHyperVManager hyperVManager,
            ICloningIsoDownloader cloningIsoDownloader,
            IEnumerable<IVmCreationStrategy> strategies,
            IVmPathService pathService,
            IVmMetadataRecorder? metadataRecorder = null)
        {
            _metadataRecorder = metadataRecorder;
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
            IProgress<CreateVMProgressInfo>? progress,
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

            // A VM that already carries this exact name is not ours: failure
            // cleanup must never remove it or its disks.
            bool vmPreexisted = false;
            IDeploymentLogger deploymentLogger = new DeploymentLogger(plan.VmName);
            try
            {
                _logger.LogInformation("Starting VM deployment for {VMName}", plan.VmName);

                cancellationToken.ThrowIfCancellationRequested();
                if (await _hyperVManager.VmExistsAsync(plan.VmName, cancellationToken))
                {
                    vmPreexisted = true;
                    throw new InvalidOperationException($"A VM named '{plan.VmName}' already exists.");
                }
                if (plan.ReplacePreviousVm)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ReplacePreviousVmAsync(plan, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();

                IProgress<CreateVMProgressInfo> effectiveProgress = progress ?? new Progress<CreateVMProgressInfo>(_ => { });
                IMediaHandler mediaHandler = _mediaHandlerFactory.CreateHandler(
                    DiskFileDetector.ResolveDiskImageFormat(galleryItem, sourceFileOrUri));
                DiskImageFormat actualFileType = mediaHandler.FileType;

                Task isoProvisioningTask = EnsureCloningIsoAsync(plan, cancellationToken, effectiveProgress);

                cancellationToken.ThrowIfCancellationRequested();
                string diskDirectory = _pathService.GetVirtualHardDiskPath(plan.VmName);
                // Archive extraction creates this directory as a side effect;
                // bare disk images (ISO, QCOW2, VHDX — no extraction step)
                // would otherwise hand qemu-img / the disk attach a path whose
                // parent doesn't exist.
                Directory.CreateDirectory(diskDirectory);
                MediaPreparationResult mediaResult = await mediaHandler.PrepareMediaAsync(
                    sourceFileOrUri,
                    diskDirectory,
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

                // The VM is deployed: nothing past this point may fail it or
                // be cancelled into the failure cleanup, which would delete it.
                await PruneDeploymentIntermediatesAsync(plan.VmName);
                if (plan.CreateCleanCheckpoint && strategyContext.GuestReady)
                    await CreateCleanCheckpointAsync(plan.VmName, deploymentLogger);
                await RecordMetadataAsync(plan, galleryItem, deploymentLogger);

                WriteDeploymentLog(deploymentLogger, LogLevel.Information);
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
                WriteDeploymentLog(deploymentLogger, LogLevel.Warning);
                await CleanupFailedVmAsync(plan, vmPreexisted, preserveInDebug: DebugPreservation.IsEnabled);
                return new VmDeploymentResult(plan.VmName, success: false, errorMessage: "Deployment cancelled.", deploymentLog: deploymentLogger.GetLog());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deploying VM: {Message}", ex.Message);
                WriteDeploymentLog(deploymentLogger, LogLevel.Warning);
                await CleanupFailedVmAsync(plan, vmPreexisted, preserveInDebug: DebugPreservation.IsEnabled);
                return new VmDeploymentResult(plan.VmName, success: false, errorMessage: ex.Message, deploymentLog: deploymentLogger.GetLog());
            }
        }

        /// <summary>
        /// Copies the per-deployment log into the application log. Some
        /// entries exist nowhere else — the ISO boot-cycle's guest scenario
        /// output and its guest diagnostics on failure (the guest-side log
        /// dies with the boot flip) — and nothing else persists
        /// <see cref="VmDeploymentResult.DeploymentLog"/>. Warning level on
        /// failure keeps the diagnostics next to the error.
        /// </summary>
        private void WriteDeploymentLog(IDeploymentLogger deploymentLogger, LogLevel level)
        {
            if (deploymentLogger.Entries.Count == 0)
                return;
            _logger.Log(level, "{DeploymentLog}", deploymentLogger.GetLog().TrimEnd());
        }

        private async Task EnsureCloningIsoAsync(
            VmDeploymentPlan plan,
            CancellationToken cancellationToken,
            IProgress<CreateVMProgressInfo> progress)
        {
            await _cloningIsoDownloader.EnsureIsoAsync(plan, cancellationToken, progress);
        }

        private async Task CleanupFailedVmAsync(VmDeploymentPlan plan, bool vmPreexisted, bool preserveInDebug)
        {
            if (preserveInDebug)
            {
                _logger.LogWarning("Debug mode: skipping cleanup for {VMName} — VM preserved for investigation", plan.VmName);
                return;
            }

            string vmName = plan.VmName;
            if (vmPreexisted)
            {
                _logger.LogWarning("Skipping cleanup for {VMName}: the VM existed before this deployment", vmName);
                return;
            }

            _logger.LogInformation("Cleaning up after failed/cancelled deployment: {VMName}", vmName);
            try
            {
                // Ask Hyper-V rather than trusting a flag: a strategy that
                // throws after New-VM leaves a registered VM behind, and that
                // is exactly the case this cleanup exists for.
                bool vmExists;
                try { vmExists = await _hyperVManager.VmExistsAsync(vmName, CancellationToken.None); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cleanup: could not query Hyper-V for {VMName}", vmName);
                    vmExists = false;
                }

                if (vmExists)
                {
                    string[] diskPaths = await GetDiskChainBestEffortAsync(vmName);

                    try { await _hyperVManager.StopVMAsync(vmName, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Stop VM during cleanup (may not be running)"); }

                    try { await _hyperVManager.RemoveVMAsync(vmName, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove VM {VMName} during cleanup", vmName); }

                    DeleteDisks(diskPaths, "Cleanup");
                }

                // Media that lives outside the per-VM directory: the legacy
                // converted-disk location, the MBR-clone target disk and the
                // NoCloud/cidata seed. Any of them can exist unattached when
                // the failure struck between creating and attaching it.
                DeleteDisks(new[]
                {
                    Path.Combine(_pathService.DefaultVhdxPath, vmName + ".vhdx"),
                    Path.Combine(_pathService.DefaultVhdxPath, vmName + "_boot.vhdx"),
                    Path.Combine(_pathService.DefaultVhdxPath, vmName + "_cidata.iso"),
                }, "Cleanup");

                DeleteDirectory(_pathService.GetVirtualHardDiskPath(vmName), "Cleanup: deleted VM disk directory");
                DeleteDirectory(Path.Combine(_pathService.DefaultVmPath, vmName), "Cleanup: deleted VM config folder");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup encountered an unexpected error for {VMName}", vmName);
            }
        }

        /// <summary>
        /// After a successful deployment, deletes everything in the per-VM
        /// disk directory that the VM does not use: the extracted source of a
        /// converted disk (Rocky's 10 GB .vhdfixed, Parrot's .qcow2), the
        /// detached source of an MBR clone, archive side files. The directory
        /// is created for this deployment's (timestamped) VM name, so anything
        /// in it that is not attached is a leftover.
        /// </summary>
        private async Task PruneDeploymentIntermediatesAsync(string vmName)
        {
            string diskDirectory = _pathService.GetVirtualHardDiskPath(vmName);
            if (!Directory.Exists(diskDirectory))
                return;

            try
            {
                var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string p in await _hyperVManager.GetVmDiskChainPathsAsync(vmName, CancellationToken.None))
                    inUse.Add(Path.GetFullPath(p));
                foreach (string p in await _hyperVManager.GetVmDvdMediaPathsAsync(vmName, CancellationToken.None))
                    inUse.Add(Path.GetFullPath(p));

                // Every deployed VM has a disk or a DVD. An empty answer means
                // the query went wrong, and pruning on it would delete the
                // VM's own disk.
                if (inUse.Count == 0)
                {
                    _logger.LogWarning("Not pruning {Directory}: Hyper-V reported no media for {VMName}", diskDirectory, vmName);
                    return;
                }

                foreach (string file in Directory.EnumerateFiles(diskDirectory, "*", SearchOption.AllDirectories).ToList())
                {
                    if (inUse.Contains(Path.GetFullPath(file)))
                        continue;
                    try
                    {
                        File.Delete(file);
                        _logger.LogInformation("Deleted deployment intermediate {Path}", file);
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete deployment intermediate {Path}", file); }
                }

                foreach (string dir in Directory.EnumerateDirectories(diskDirectory, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length).ToList())
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune deployment intermediates in {Directory}", diskDirectory);
            }
        }

        internal const string CleanCheckpointName = "Clean deploy (VMCreate)";

        private async Task CreateCleanCheckpointAsync(string vmName, IDeploymentLogger deploymentLogger)
        {
            try
            {
                await _hyperVManager.CreateCheckpointAsync(vmName, CleanCheckpointName, CancellationToken.None);
                deploymentLogger.Log($"Created checkpoint '{CleanCheckpointName}'");
            }
            catch (Exception ex)
            {
                // The VM itself is fine; a missing checkpoint is not worth
                // failing — and deleting — a finished deployment over.
                _logger.LogWarning(ex, "Could not create the '{Checkpoint}' checkpoint for {VMName}", CleanCheckpointName, vmName);
                deploymentLogger.LogWarning($"Could not create checkpoint '{CleanCheckpointName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Records provenance, folder and tags for VMCreate Manager. Best
        /// effort, like the checkpoint: the VM is finished and must not be
        /// failed (and deleted) over bookkeeping.
        /// </summary>
        private async Task RecordMetadataAsync(VmDeploymentPlan plan, GalleryItem galleryItem, IDeploymentLogger deploymentLogger)
        {
            if (_metadataRecorder == null)
                return;
            try
            {
                await _metadataRecorder.RecordDeploymentAsync(plan, galleryItem, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record VMCreate Manager metadata for {VMName}", plan.VmName);
                deploymentLogger.LogWarning($"Could not record folder/tags/provenance: {ex.Message}");
            }
        }

        private async Task<string[]> GetDiskChainBestEffortAsync(string vmName)
        {
            try
            {
                return await _hyperVManager.GetVmDiskChainPathsAsync(vmName, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the disk chain of {VMName}; falling back to the attached disks", vmName);
                try { return await _hyperVManager.GetVmHardDiskPathsAsync(vmName, CancellationToken.None); }
                catch { return Array.Empty<string>(); }
            }
        }

        private void DeleteDisks(IEnumerable<string> paths, string context)
        {
            foreach (string path in paths)
            {
                if (!File.Exists(path))
                    continue;
                if (!path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                    TryDismountVhdx(path);
                try
                {
                    File.Delete(path);
                    _logger.LogInformation("{Context}: deleted {Path}", context, path);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "{Context}: failed to delete {Path}", context, path); }
            }
        }

        private void DeleteDirectory(string path, string message)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    _logger.LogInformation("{Message} {Path}", message, path);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete folder {Path}", path); }
        }

        private void TryDismountVhdx(string vhdxPath)
        {
            // Shared best-effort dismount helper (CreateDefault2 + Hyper-V
            // runspace); media preparation uses the same one so the two
            // paths cannot drift apart. Cleanup logs a successful
            // dismount at Information (the disk is gone — worth knowing);
            // failures stay Debug in the helper.
            if (HostPowerShell.TryDismountVhdx(vhdxPath, _logger))
                _logger.LogInformation("Dismounted VHDX during cleanup: {VhdxPath}", vhdxPath);
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

                // The whole chain, not just the attached disks: with a
                // checkpoint the attached disk is an .avhdx, and deleting only
                // that would strand the parent .vhdx underneath. Read before
                // Remove-VM, which drops the checkpoint records.
                string[] vhdxPaths = await _hyperVManager.GetVmDiskChainPathsAsync(existingVmName, cancellationToken);

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

                // The per-VM disk directory is where extraction and conversion
                // wrote the deployment's media. Files attached to the VM were
                // deleted above, but a FAILED earlier deploy can leave
                // unattached intermediates behind (partial extraction,
                // half-converted disks, the downloaded archive). A stale
                // directory at the same path breaks the next deploy of this
                // VM name — remove the whole per-VM directory so re-deploys
                // start from a clean slate (the extractor recreates it).
                string oldDiskDir = _pathService.GetVirtualHardDiskPath(existingVmName);
                try
                {
                    if (Directory.Exists(oldDiskDir))
                    {
                        Directory.Delete(oldDiskDir, true);
                        _logger.LogInformation("Deleted old VM disk directory: {Folder}", oldDiskDir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete VM disk directory {Folder}: {Message}", oldDiskDir, ex.Message);
                }
            }
        }
    }
}
