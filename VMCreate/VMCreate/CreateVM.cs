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
        private readonly IExtractor _extractor;
        private readonly IVmCreator _vmCreator;
        private readonly ILogger<CreateVM> _logger;
        private bool _useCache = true;

        public CreateVM(IDownloader downloader, IExtractor extractor, IVmCreator vmCreator, ILogger<CreateVM> logger, IOptions<AppSettings> options)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
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
                if(!galleryItem.FileType.StartsWith("vhd") && !File.Exists(_qemuFileLocation))
                {
                    throw new Exception("Please install QEMU to support disk image conversion.");
                }

                _logger.LogInformation("Starting VM creation for {VMName}", vmSettings.VMName);

                // Download file
                createVmProgressInfo.Report(new CreateVMProgressInfo { Phase = "Download" });
                filename = await _downloader.DownloadFileAsync(galleryItem.DiskUri, cancellationToken, createVmProgressInfo, _useCache);
                _logger.LogInformation("Downloaded file {FileName}", filename);

                // Extract if needed
                if (galleryItem.FileType is not ("ISO" or "QCOW2"))
                {
                    createVmProgressInfo.Report(new CreateVMProgressInfo { Phase = "Extract" });
                    await Task.Run(() => _extractor.Extract(filename, _extractPath, cancellationToken, createVmProgressInfo));
                    _logger.LogInformation("Extracted file to {ExtractPath}", _extractPath);

                    // Create VM
                    await _vmCreator.CreateVMAsync(vmSettings, vmCustomizations, Path.Combine(_extractPath, galleryItem.ArchiveRelativePath ?? throw new Exception("ArchiveRelativePath is null")), galleryItem, cancellationToken, createVmProgressInfo);
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