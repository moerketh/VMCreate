using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VMCreate;

namespace VMCreate
{
    public interface IDownloader
    {
        Task<string> DownloadFileAsync(string uri, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo, bool useCache);
    }

    public class HttpFileDownloader : IDownloader
    {
        private readonly ILogger<HttpFileDownloader> _logger;

        public HttpFileDownloader(ILogger<HttpFileDownloader> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<string> DownloadFileAsync(string uri, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo, bool useCache)
        {
            const int maxRetries = 3;
            int attempt = 0;
            string fileName = string.Empty;

            while (attempt < maxRetries)
            {
                try
                {
                    attempt++;
                    _logger.LogInformation("Download attempt {Attempt} for URI: {Uri}", attempt, uri);
                    using (HttpClient client = new HttpClient())
                    {
                        client.DefaultRequestHeaders.Add("User-Agent", "VMCreateVM");
                        DateTime startTime = DateTime.Now;
                        using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();
                            // Capture the final URI after redirects
                            string finalUri = response.RequestMessage.RequestUri.ToString();
                            _logger.LogInformation("Final URI after redirects: {FinalUri}", finalUri);
                            long? contentLength = response.Content.Headers.ContentLength;
                            _logger.LogInformation("Content-Length: {ContentLength} bytes", contentLength);
                            fileName = finalUri.Split('/').Last();
                            fileName = Path.Combine(Path.GetTempPath(), fileName);
                            if (File.Exists(fileName) && useCache)
                            {
                                _logger.LogInformation("Using cached file: {FileName}", fileName);
                                return fileName;
                            }

                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                            {
                                long totalBytesRead = 0;
                                byte[] buffer = new byte[65536];
                                int bytesRead;
                                DateTime lastUpdate = DateTime.Now;
                                long lastBytesRead = 0;

                                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    totalBytesRead += bytesRead;

                                    if ((DateTime.Now - lastUpdate).TotalMilliseconds >= 1000)
                                    {
                                        var progressInfo = new CreateVMProgressInfo
                                        {
                                            URI = finalUri, // Use the final URI after redirects
                                            DownloadSpeed = (totalBytesRead - lastBytesRead) / (DateTime.Now - lastUpdate).TotalSeconds / 1024 / 1024, // Speed in MB/s
                                            ProgressPercentage = Convert.ToInt32(contentLength.HasValue ? (double)totalBytesRead / contentLength.Value * 100 : 0)
                                        };
                                        progressReportInfo.Report(progressInfo);
                                        lastUpdate = DateTime.Now;
                                        lastBytesRead = totalBytesRead;

                                        cancellationToken.ThrowIfCancellationRequested();
                                    }
                                }

                                double duration = (DateTime.Now - startTime).TotalSeconds;
                                double avgSpeedMBps = contentLength.HasValue ? (contentLength.Value / duration) / 1024 / 1024 : 0;
                                _logger.LogInformation("Download completed in {Duration:F2} seconds. Average speed: {AvgSpeed:F2} MB/s", duration, avgSpeedMBps);
                            }
                        }
                    }
                    return fileName;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Download cancelled for URI: {Uri}", uri);
                    if (File.Exists(fileName)) File.Delete(fileName);
                    _logger.LogWarning("Deleted temporary download file: {fileName}", fileName);
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "Download attempt {Attempt} failed for URI: {Uri}", attempt, uri);
                    if (attempt >= maxRetries)
                    {
                        throw new Exception($"Failed to download after {maxRetries} attempts: {ex.Message}", ex);
                    }
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during download attempt {Attempt} for URI: {Uri}", attempt, uri);
                    throw;
                }
            }
            return fileName;
        }
    }
}