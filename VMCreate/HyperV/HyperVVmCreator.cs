using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV.VmCreation;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    public interface IVmCreator
    {
        /// <summary>
        /// Returns the directory where VM disk files should be created.
        /// Used by the extraction flow to avoid a wasteful temp→destination copy.
        /// </summary>
        string GetVirtualHardDiskPath();

        /// <summary>
        /// Returns the per-VM subdirectory under GetVirtualHardDiskPath() where archives
        /// and intermediate disk files for this VM should be extracted.
        /// </summary>
        string GetVirtualHardDiskPath(string vmName);

        Task CreateVMAsync(VmSettings vmSettings, VmCustomizations vmCustomizations, string sourceFile, GalleryItem galleryItem, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> downloadProgressInfo);
    }

    public class HyperVVmCreator : IVmCreator
    {
        private readonly IVmPathService _pathService;
        private readonly IMediaHandlerFactory _mediaHandlerFactory;
        private readonly IHyperVManager _hyperVManager;
        private readonly ILogger<HyperVVmCreator> _logger;
        private readonly ICloningIsoDownloader _cloningIsoDownloader;
        private readonly IEnumerable<IVmCreationStrategy> _strategies;

        public HyperVVmCreator(
            ILogger<HyperVVmCreator> logger,
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

        public string GetVirtualHardDiskPath() => _pathService.DefaultVhdxPath;

        public string GetVirtualHardDiskPath(string vmName) => _pathService.GetVirtualHardDiskPath(vmName);

        public async Task CreateVMAsync(VmSettings vmSettings, VmCustomizations vmCustomizations, string sourceFile, GalleryItem item, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> createVMProgressInfo)
        {
            bool vmCreated = false;
            try
            {
                _logger.LogInformation("Starting VM creation for {VMName}", vmSettings.VMName);

                if (vmSettings.ReplacePreviousVm)
                {
                    await ReplacePreviousVmAsync(vmSettings, cancellationToken);
                }

                string actualFileType = DiskFileDetector.DetectFileType(sourceFile);
                if (actualFileType is "Unknown" or "Other")
                    actualFileType = item.FileType;

                IMediaHandler mediaHandler = _mediaHandlerFactory.CreateHandler(actualFileType);
                bool isIsoMedia = mediaHandler is IsoMediaHandler;

                Task ensureIsoTask = null;
                if (!isIsoMedia && !item.IsNativeHyperV)
                {
                    ensureIsoTask = _cloningIsoDownloader.EnsureIsoAsync(vmSettings, cancellationToken, createVMProgressInfo);
                }

                string mediaPath = await mediaHandler.PrepareMediaAsync(sourceFile, _pathService.DefaultVhdxPath, vmSettings, item, createVMProgressInfo, cancellationToken);

                var strategy = _strategies.FirstOrDefault(s => s.CanHandle(item, actualFileType))
                    ?? throw new NotSupportedException($"No creation strategy found for file type '{actualFileType}' and gallery item '{item.Name}'.");

                if (ensureIsoTask != null)
                    await ensureIsoTask;

                var context = new VmCreationContext(
                    vmSettings,
                    vmCustomizations,
                    sourceFile,
                    item,
                    mediaHandler,
                    mediaPath,
                    cancellationToken,
                    createVMProgressInfo);

                vmCreated = true;
                await strategy.CreateVMAsync(context);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("VM creation cancelled for {VMName} — cleaning up", vmSettings.VMName);
                if (!System.Diagnostics.Debugger.IsAttached)
                    await CleanupFailedVmAsync(vmSettings, vmCreated);
                else
                    _logger.LogWarning("Debug mode: skipping cleanup for {VMName} — VM preserved for investigation", vmSettings.VMName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating VM: {Message}", ex.Message);
                if (!System.Diagnostics.Debugger.IsAttached)
                    await CleanupFailedVmAsync(vmSettings, vmCreated);
                else
                    _logger.LogWarning("Debug mode: skipping cleanup for {VMName} — VM preserved for investigation", vmSettings.VMName);
                throw;
            }
        }

        private async Task CleanupFailedVmAsync(VmSettings vmSettings, bool vmCreated)
        {
            string vmName = vmSettings.VMName;
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
                using var ps = PowerShell.Create();
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

        private async Task ReplacePreviousVmAsync(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            string fullName = vmSettings.VMName;
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
