using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
        private readonly ILogger<CreateVM> _logger;
        private bool _useCache = true;

        public CreateVM(IDownloader downloader, IChecksumVerifier checksumVerifier, IExtractor extractor, DiskFileDetector diskFileDetector, IVmCreator vmCreator, ILogger<CreateVM> logger, IOptions<AppSettings> options)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _checksumVerifier = checksumVerifier ?? throw new ArgumentNullException(nameof(checksumVerifier));
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
            _diskFileDetector = diskFileDetector ?? throw new ArgumentNullException(nameof(diskFileDetector));
            _vmCreator = vmCreator ?? throw new ArgumentNullException(nameof(vmCreator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            var settings = options?.Value ?? new AppSettings();
            _qemuFileLocation = settings.QemuImgPath;
            _extractPath = settings.ExtractPath;
        }

        public async Task StartCreateVMAsync(VmSettings vmSettings, VmCustomizations vmCustomizations, GalleryItem galleryItem, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> createVmProgressInfo)
        {
            string filename = string.Empty;
            try
            {
                if(!galleryItem.FileType.StartsWith("vhd", StringComparison.OrdinalIgnoreCase)
                    && galleryItem.FileType != "ISO"
                    && !File.Exists(_qemuFileLocation))
                {
                    throw new Exception("Please install QEMU to support disk image conversion.");
                }

                // Download file
                createVmProgressInfo.Report(new CreateVMProgressInfo { Phase = "Download" });
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

                // Extract if needed — archives (OVA, ZIP, 7Z, etc.) and compressed disks (vmdk.xz)
                // need extraction. ISO and QCOW2 are used directly.
                bool needsExtraction = galleryItem.FileType is not ("ISO" or "QCOW2" or "VHDX" or "VHD");
                if (needsExtraction)
                {
                    createVmProgressInfo.Report(new CreateVMProgressInfo { Phase = "Extract" });
                    await Task.Run(() => _extractor.Extract(filename, _extractPath, cancellationToken, createVmProgressInfo));
                    _logger.LogInformation("Extracted file to {ExtractPath}", _extractPath);

                    // Auto-detect the disk file inside the extracted directory.
                    // Handles nested archives (e.g. OVA inside ZIP) automatically.
                    string diskFile = await Task.Run(() =>
                        _diskFileDetector.FindDiskFile(_extractPath, cancellationToken, createVmProgressInfo));
                    _logger.LogInformation("Detected disk file {DiskFile}", diskFile);

                    // Create VM using the detected disk file's actual type
                    await _vmCreator.CreateVMAsync(vmSettings, vmCustomizations, diskFile, galleryItem, cancellationToken, createVmProgressInfo);
                    _logger.LogInformation("Successfully created VM {VMName}", vmSettings.VMName);
                }
                else
                {
                    // Create VM
                    await _vmCreator.CreateVMAsync(vmSettings, vmCustomizations, filename, galleryItem, cancellationToken, createVmProgressInfo);
                    _logger.LogInformation("Successfully created VM {VMName}", vmSettings.VMName);
                }
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

                if (Directory.Exists(_extractPath))
                {
                    Directory.Delete(_extractPath, true);
                    _logger.LogDebug("Deleted temporary directory {ExtractPath}", _extractPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temporary files");
            }
        }
    }
}