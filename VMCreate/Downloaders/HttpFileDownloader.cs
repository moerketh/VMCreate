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

                    var (contentStream, contentLength, finalUri) = await _streamProvider.GetStreamAsync(uri, cancellationToken);

                    var parsedUri = new Uri(finalUri);
                    var rawFileName = Path.GetFileName(parsedUri.LocalPath);
                    rawFileName = new string(rawFileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
                    filePath = Path.Combine(Path.GetTempPath(), rawFileName);

                    var (writeStream, isCached) = await _fileStreamProvider.GetWriteStreamAsync(filePath, useCache);

                    if (isCached)
                    {
                        return filePath;
                    }

                    using (contentStream)
                    using (writeStream)
                    {
                        await _streamCopier.CopyAsync(contentStream, writeStream, contentLength, finalUri, progressReportInfo, cancellationToken);
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