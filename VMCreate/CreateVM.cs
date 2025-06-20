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
        private readonly string logPath = Path.Combine(Path.GetTempPath(), "VMCreate.log");
        private readonly IDownloader _downloader;
        private readonly IExtractor _extractor;
        private readonly IVmCreator _vmCreator;

        public CreateVM(IDownloader downloader, IExtractor extractor, IVmCreator vmCreator)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
            _vmCreator = vmCreator ?? throw new ArgumentNullException(nameof(vmCreator));
        }
        private void WriteLog(string message)
        {
            try
            {
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch { }
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
                        return response.Headers.Location.ToString();
                    }
                    return uri;
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error resolving mirror URI: {ex.Message}");
                return uri;
            }
        }

        public async Task StartCreateVMAsync(VmSettings vmSettings, GalleryItem galleryItem, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> createVmProgressInfo)
        {
            var filename = string.Empty;
            try
            {
                //resolve mirror link
                var mirrorUri = ResolveMirrorUri(galleryItem.DiskUri);
                // Download file  
                filename = await _downloader.DownloadFileAsync(mirrorUri, cancellationToken, createVmProgressInfo);
                // Transition to unpacking
                await Task.Run(() => _extractor.Extract(filename, extractPath, cancellationToken, createVmProgressInfo));
                // Create VM
                await _vmCreator.CreateVMAsync(vmSettings, extractPath, galleryItem, cancellationToken, createVmProgressInfo);
            }
            catch (Exception ex)
            {
                WriteLog($"Error in VM creation process: {ex.Message}");
                throw;
            }
            finally
            {
                //CleanupTempFiles(filename, true);
            }
        }

        private void CleanupTempFiles(string filename, bool keepCache)
        {
            try
            {
                if (File.Exists(filename) && !keepCache)
                {
                    File.Delete(filename);
                    WriteLog($"Deleted temporary file: {filename}");
                }
                else
                {
                    WriteLog($"Keeping temporary file: {filename}");
                }
                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                    WriteLog($"Deleted temporary directory: {extractPath}");
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error cleaning up temporary files: {ex.Message}");
            }
        }

    }
}
