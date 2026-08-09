using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV;
using VMCreate.HyperV.VmCreation;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    public class CreateVM
    {
        private readonly string _qemuFileLocation;
        private readonly string _extractPath;
        private readonly IDownloader _downloader;
        private readonly IChecksumVerifier _checksumVerifier;
        private readonly IExtractor _extractor;
        private readonly DiskFileDetector _diskFileDetector;
        private readonly IVmCreator _vmCreator;
        private readonly IVmPathService _pathService;
        private readonly ILogger<CreateVM> _logger;
        private bool _useCache = true;

        public CreateVM(
            IDownloader downloader,
            IChecksumVerifier checksumVerifier,
            IExtractor extractor,
            DiskFileDetector diskFileDetector,
            IVmCreator vmCreator,
            IVmPathService pathService,
            ILogger<CreateVM> logger,
            IOptions<AppSettings> options)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _checksumVerifier = checksumVerifier ?? throw new ArgumentNullException(nameof(checksumVerifier));
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
            _diskFileDetector = diskFileDetector ?? throw new ArgumentNullException(nameof(diskFileDetector));
            _vmCreator = vmCreator ?? throw new ArgumentNullException(nameof(vmCreator));
            _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            var settings = options?.Value ?? new AppSettings();
            _qemuFileLocation = settings.QemuImgPath;
            _extractPath = settings.ExtractPath;
        }

        public async Task<string> StartCreateVMAsync(
            VmSettings vmSettings,
            VmCustomizations vmCustomizations,
            GalleryItem galleryItem,
            CancellationToken cancellationToken,
            IProgress<CreateVMProgressInfo> createVmProgressInfo)
        {
            string filename = string.Empty;
            try
            {
                var format = DiskFileDetector.DetectFileType(galleryItem.DiskUri);
                if(!galleryItem.IsNativeHyperV
                    && format != DiskImageFormat.Vhdx
                    && format != DiskImageFormat.Vhd
                    && format != DiskImageFormat.Iso
                    && !File.Exists(_qemuFileLocation))
                {
                    throw new Exception("Please install QEMU to support disk image conversion.");
                }

                // Compute the effective, timestamped VM name once and freeze it into an
                // immutable plan. The wizard's VmSettings are never mutated.
                string baseName = vmSettings.VMName;
                string effectiveVmName = $"{baseName}_{DateTime.Now:yyyyMMddHHmmss}";
                VmDeploymentPlan plan = VmDeploymentPlan.FromSettings(vmSettings)
                    .WithVmName(effectiveVmName);

                createVmProgressInfo?.Report(CreateVMProgressInfo.ForPhase(
                    VmDeploymentPhase.None,
                    VmDeploymentSubStep.None,
                    effectiveVmName));

                // Preflight: verify the user has Hyper-V Administrators group membership,
                // which is required for WMI namespace access (New-VM, New-VHD, etc.).
                // Check before the download to avoid wasting a large download on a user
                // who will inevitably fail at VM creation.
                if (!_pathService.IsHyperVAdministrator())
                {
                    var userName = Environment.UserName;
                    throw new HyperVPermissionException(
                        $"You must be a member of the 'Hyper-V Administrators' group to create VMs.\n" +
                        $"Run the following command as Administrator, then log out and back in:\n" +
                        $"    Add-LocalGroupMember -Group 'Hyper-V Administrators' -Member '{userName}'");
                }

                // Download file
                createVmProgressInfo.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.Download));
                filename = await _downloader.DownloadFileAsync(galleryItem.DiskUri, cancellationToken, createVmProgressInfo, _useCache);
                _logger.LogInformation("Downloaded file {FileName}", filename);

                // Verify checksum if configured (inline hash takes precedence over URI)
                if (!string.IsNullOrEmpty(galleryItem.Checksum))
                {
                    await _checksumVerifier.VerifyInlineAsync(
                        filename, galleryItem.Checksum, galleryItem.ChecksumAlgorithm,
                        cancellationToken, createVmProgressInfo);
                }
                else if (!string.IsNullOrEmpty(galleryItem.ChecksumUri))
                {
                    await _checksumVerifier.VerifyAsync(
                        filename, galleryItem.ChecksumUri, galleryItem.ChecksumAlgorithm,
                        cancellationToken, createVmProgressInfo);
                }

                // Extract if needed — archives (OVA, ZIP, 7Z, etc.) and compressed disks
                // (vmdk.xz, vhdx.zip) need extraction. Bare ISO/QCOW2/VHDX/VHD are used directly.
                bool needsExtraction = galleryItem.NeedsExtraction;
                string sourceFile;
                if (needsExtraction)
                {
                    createVmProgressInfo.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.Extract));

                    // Determine the final disk directory so we can extract directly there
                    // and avoid a wasteful temp→destination copy. Use a per-VM subdirectory
                    // so previous deployments can't lock or collide with this one.
                    string extractDest = _pathService.GetVirtualHardDiskPath(plan.VmName);
                    _logger.LogInformation("Extracting directly to per-VM VM disk directory: {Path}", extractDest);

                    await Task.Run(() => _extractor.Extract(filename, extractDest, cancellationToken, createVmProgressInfo));
                    _logger.LogInformation("Extracted file to {ExtractPath}", extractDest);

                    // Auto-detect the disk file inside the extracted directory.
                    // Handles nested archives (e.g. OVA inside ZIP) automatically.
                    sourceFile = await Task.Run(() =>
                        _diskFileDetector.FindDiskFile(extractDest, cancellationToken, createVmProgressInfo));
                    _logger.LogInformation("Detected disk file {DiskFile}", sourceFile);
                }
                else
                {
                    sourceFile = filename;
                }

                // Create VM from the prepared source file using the immutable plan.
                VmDeploymentResult result = await _vmCreator.CreateAsync(plan, vmCustomizations, galleryItem, cancellationToken, createVmProgressInfo, sourceFile);
                if (!result.Success)
                {
                    throw new InvalidOperationException(
                        $"VM creation failed for {result.VmName}: {result.ErrorMessage}");
                }

                _logger.LogInformation("Successfully created VM {VMName}", result.VmName);

                return result.VmName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create VM {VMName}", vmSettings.VMName);
                throw;
            }
            finally
            {
                CleanupTempFiles(filename, _useCache);
            }
        }

        private void CleanupTempFiles(string filename, bool useCache)
        {
            try
            {
                if (File.Exists(filename) && !useCache)
                {
                    File.Delete(filename);
                    _logger.LogDebug("Deleted temporary file {FileName}", filename);
                }
                else
                {
                    _logger.LogDebug("Keeping temporary file {FileName}", filename);
                }

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temporary files");
            }
        }
    }
}
