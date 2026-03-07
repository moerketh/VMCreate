using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    public interface IDownloader
    {
        Task<string> DownloadFileAsync(string uri, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo, bool useCache);
    }

    public class HttpFileDownloader : IDownloader
    {
        private readonly ILogger<HttpFileDownloader> _logger;
        private readonly IHttpStreamProvider _streamProvider;
        private readonly IFileStreamProvider _fileStreamProvider;
        private readonly IStreamCopierWithProgress _streamCopier;

        public HttpFileDownloader(ILogger<HttpFileDownloader> logger, IHttpStreamProvider streamProvider, IFileStreamProvider fileStreamProvider, IStreamCopierWithProgress streamCopier)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
            _fileStreamProvider = fileStreamProvider ?? throw new ArgumentNullException(nameof(fileStreamProvider));
            _streamCopier = streamCopier ?? throw new ArgumentNullException(nameof(streamCopier));
        }

        public async Task<string> DownloadFileAsync(string uri, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo, bool useCache)
        {
            const int MaxRetries = 3;
            int attempt = 0;
            string filePath = string.Empty;

            while (attempt < MaxRetries)
            {
                try
                {
                    attempt++;
                    _logger.LogInformation("Download attempt {Attempt} for URI: {Uri}", attempt, uri);

                    DateTime startTime = DateTime.Now;

                    using var response = await _streamProvider.GetResponseAsync(uri, cancellationToken);

                    string finalUri = response.RequestMessage.RequestUri.ToString();
                    long? contentLength = response.Content.Headers.ContentLength;

                    var parsedUri = new Uri(finalUri);
                    var rawFileName = Path.GetFileName(parsedUri.LocalPath);
                    rawFileName = new string(rawFileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
                    filePath = Path.Combine(Path.GetTempPath(), rawFileName);

                    _logger.LogInformation("Final URI after redirects: {FinalUri}", finalUri);
                    if (contentLength.HasValue)
                        _logger.LogInformation("Content-Length: {ContentLength} bytes", contentLength.Value);

                    // Validate cached file size against Content-Length before reusing
                    if (useCache && File.Exists(filePath))
                    {
                        long cachedSize = new FileInfo(filePath).Length;
                        if (contentLength.HasValue && cachedSize == contentLength.Value)
                        {
                            _logger.LogInformation("Using cached file: {FilePath} ({Size} bytes, matches server)", filePath, cachedSize);
                            return filePath;
                        }
                        else
                        {
                            _logger.LogWarning("Cached file size mismatch for {FilePath}: cached={CachedSize}, expected={Expected}. Re-downloading.",
                                filePath, cachedSize, contentLength?.ToString() ?? "unknown");
                            File.Delete(filePath);
                        }
                    }

                    var writeStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

                    var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

                    using (writeStream)
                    {
                        await _streamCopier.CopyAsync(contentStream, writeStream, contentLength, finalUri, progressReportInfo, cancellationToken);
                    }

                    // Post-download size verification
                    if (contentLength.HasValue)
                    {
                        long actualSize = new FileInfo(filePath).Length;
                        if (actualSize != contentLength.Value)
                        {
                            _logger.LogError("Download size mismatch: expected {Expected} bytes, got {Actual} bytes. Deleting corrupt file.",
                                contentLength.Value, actualSize);
                            File.Delete(filePath);
                            throw new IOException($"Downloaded file is incomplete: expected {contentLength.Value} bytes but received {actualSize} bytes.");
                        }
                    }

                    double duration = (DateTime.Now - startTime).TotalSeconds;
                    double avgSpeedMBps = contentLength.HasValue ? (contentLength.Value / duration) / 1024 / 1024 : 0;
                    _logger.LogInformation("Download completed in {Duration:F2} seconds. Average speed: {AvgSpeed:F2} MB/s", duration, avgSpeedMBps);

                    return filePath;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Download cancelled for URI: {Uri}", uri);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        _logger.LogWarning("Deleted temporary download file: {FilePath}", filePath);
                    }
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "Download attempt {Attempt} failed for URI: {Uri}", attempt, uri);
                    if (attempt >= MaxRetries)
                    {
                        throw new Exception($"Failed to download after {MaxRetries} attempts: {ex.Message}", ex);
                    }
                    await Task.Delay(1000 * (int)Math.Pow(2, attempt - 1), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during download attempt {Attempt} for URI: {Uri}", attempt, uri);
                    throw;
                }
            }
            return filePath;
        }
    }
}