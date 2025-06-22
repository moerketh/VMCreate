using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate
{
    public class CreateVM
    {
        private readonly string extractPath = Path.Combine(Path.GetTempPath(), "VMExtracted");
        private readonly IDownloader _downloader;
        private readonly IExtractor _extractor;
        private readonly IVmCreator _vmCreator;
        private readonly ILogger<CreateVM> _logger;
        private bool _useCache = true;

        public CreateVM(IDownloader downloader, IExtractor extractor, IVmCreator vmCreator, ILogger<CreateVM> logger)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
            _vmCreator = vmCreator ?? throw new ArgumentNullException(nameof(vmCreator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private string ResolveMirrorUri(string uri)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "VMCreateVM");
                    var request = new HttpRequestMessage(HttpMethod.Head, uri);
                    var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).Result;
                    if (response.StatusCode == System.Net.HttpStatusCode.Found || response.StatusCode == System.Net.HttpStatusCode.Moved)
                    {
                        _logger.LogDebug("Resolved mirror URI from {OriginalUri} to {ResolvedUri}", uri, response.Headers.Location.ToString());
                        return response.Headers.Location.ToString();
                    }
                    _logger.LogDebug("No mirror redirect needed for URI {Uri}", uri);
                    return uri;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve mirror URI {Uri}, using original URI", uri);
                return uri;
            }
        }

        public async Task StartCreateVMAsync(VmSettings vmSettings, GalleryItem galleryItem, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> createVmProgressInfo)
        {
            string filename = string.Empty;
            try
            {
                _logger.LogInformation("Starting VM creation for {VMName}", vmSettings.VMName);

                // Resolve mirror link
                var mirrorUri = ResolveMirrorUri(galleryItem.DiskUri);
                _logger.LogDebug("Using disk URI {DiskUri}", mirrorUri);

                // Download file
                filename = await _downloader.DownloadFileAsync(mirrorUri, cancellationToken, createVmProgressInfo, _useCache);
                _logger.LogInformation("Downloaded file {FileName}", filename);

                // Transition to unpacking
                await Task.Run(() => _extractor.Extract(filename, extractPath, cancellationToken, createVmProgressInfo));
                _logger.LogInformation("Extracted file to {ExtractPath}", extractPath);

                // Create VM
                await _vmCreator.CreateVMAsync(vmSettings, extractPath, galleryItem, cancellationToken, createVmProgressInfo);
                _logger.LogInformation("Successfully created VM {VMName}", vmSettings.VMName);
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

                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                    _logger.LogDebug("Deleted temporary directory {ExtractPath}", extractPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temporary files");
            }
        }
    }
}