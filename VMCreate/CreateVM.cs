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
        private readonly string zipFilePath = Path.Combine(Path.GetTempPath(), "vm_image.7z");
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
            try
            {
                //resolve mirror link
                var mirrorUri = ResolveMirrorUri(galleryItem.DiskUri);
                // Download file  
                await _downloader.DownloadFileAsync(mirrorUri, zipFilePath, cancellationToken, createVmProgressInfo);
                // Transition to unpacking
                await Task.Run(() => _extractor.Unpack7ZipAsync(zipFilePath, extractPath, cancellationToken, createVmProgressInfo));
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
                CleanupTempFiles();
            }
        }

        private void CleanupTempFiles()
        {
            try
            {
                if (File.Exists(zipFilePath))
                {
                    File.Delete(zipFilePath);
                    WriteLog($"Deleted temporary file: {zipFilePath}");
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
