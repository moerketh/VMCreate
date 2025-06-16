using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using VMCreate;

namespace VMCreateVM
{
    public interface IDownloader
    {
        Task DownloadFileAsync(string uri, string filePath, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo);
    }

    public class HttpFileDownloader : IDownloader
    {
        private readonly string logPath = Path.Combine(Path.GetTempPath(), "VMCreate.log");

        private void WriteLog(string message)
        {
            try
            {
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch { }
        }

        public async Task DownloadFileAsync(string uri, string filePath, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo)
        {
            const int maxRetries = 3;
            int attempt = 0;

            while (attempt < maxRetries)
            {
                try
                {
                    attempt++;
                    WriteLog($"Download attempt {attempt} for URI: {uri}");
                    using (HttpClient client = new HttpClient())
                    {
                        client.DefaultRequestHeaders.Add("User-Agent", "VMCreateVM");
                        DateTime startTime = DateTime.Now;
                        using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();
                            long? contentLength = response.Content.Headers.ContentLength;
                            WriteLog($"Content-Length: {contentLength} bytes");

                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
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
                                            URI = uri,
                                            DownloadSpeed = (totalBytesRead - lastBytesRead) / (DateTime.Now - lastUpdate).TotalSeconds / 1024 / 1024, // Speed in MB/s
                                            ProgressPercentage = Convert.ToInt32(contentLength.HasValue ? (double)totalBytesRead / contentLength.Value * 100 : 0)
                                        };
                                        progressReportInfo.Report(progressInfo);
                                        //Application.Current.Dispatcher.Invoke(() =>
                                        //{
                                        //    progressReport.Report(progress);
                                        //    WriteLog($"Progress updated: {progress:F0}%");
                                        //    //WriteLog($"Speed updated: {speedMBps:F2} MB/s");
                                        //});
                                        lastUpdate = DateTime.Now;
                                        lastBytesRead = totalBytesRead;
                                        
                                        cancellationToken.ThrowIfCancellationRequested();
                                    }
                                }

                                double duration = (DateTime.Now - startTime).TotalSeconds;
                                double avgSpeedMBps = contentLength.HasValue ? (contentLength.Value / duration) / 1024 / 1024 : 0;
                                WriteLog($"Download completed in {duration:F2} seconds. Average speed: {avgSpeedMBps:F2} MB/s");
                            }
                        }
                    }
                    return; // Success, exit retry loop
                }
                catch (OperationCanceledException)
                {
                    WriteLog("Download cancelled.");
                    throw; // Re-throw cancellation exception
                }
                catch (HttpRequestException ex)
                {
                    WriteLog($"Download attempt {attempt} failed: {ex.Message}");
                    if (attempt >= maxRetries)
                    {
                        throw new Exception($"Failed to download after {maxRetries} attempts: {ex.Message}");
                    }
                    await Task.Delay(1000); // Wait 1 second before retry
                }
                catch (Exception ex)
                {
                    WriteLog($"Unexpected error during download attempt {attempt}: {ex.Message}");
                    throw;
                }
            }
        }
    }
}