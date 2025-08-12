using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    public interface IFileWriter
    {
        Task WriteAsync(Stream sourceStream, string filePath, long? contentLength, string finalUri, IProgress<CreateVMProgressInfo> progressReporter, CancellationToken cancellationToken);
        bool TryGetCachedFile(string filePath, out string cachedFilePath);
    }

    public class FileWriter : IFileWriter
    {
        private readonly ILogger<FileWriter> _logger;
        private const int BufferSize = 65536;

        public FileWriter(ILogger<FileWriter> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool TryGetCachedFile(string filePath, out string cachedFilePath)
        {
            if (File.Exists(filePath))
            {
                _logger.LogInformation("Using cached file: {FilePath}", filePath);
                cachedFilePath = filePath;
                return true;
            }
            cachedFilePath = null;
            return false;
        }

        public async Task WriteAsync(Stream sourceStream, string filePath, long? contentLength, string finalUri, IProgress<CreateVMProgressInfo> progressReporter, CancellationToken cancellationToken)
        {
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);

            long totalBytesRead = 0;
            byte[] buffer = new byte[65536];
            int bytesRead;
            DateTime lastUpdate = DateTime.Now;
            long lastBytesRead = 0;

            progressReporter.Report(new CreateVMProgressInfo
            {
                URI = finalUri,
                DownloadSpeed = 0,
                ProgressPercentage = 0
            });

            while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalBytesRead += bytesRead;

                if ((DateTime.Now - lastUpdate).TotalMilliseconds >= 1000)
                {
                    double speed = (totalBytesRead - lastBytesRead) / (DateTime.Now - lastUpdate).TotalSeconds / 1024 / 1024; // MB/s
                    int percentage = contentLength.HasValue ? Convert.ToInt32((double)totalBytesRead / contentLength.Value * 100) : 0;

                    // Report progress
                    progressReporter.Report(new CreateVMProgressInfo
                    {
                        URI = finalUri,
                        DownloadSpeed = speed,
                        ProgressPercentage = percentage
                    });

                    lastUpdate = DateTime.Now;
                    lastBytesRead = totalBytesRead;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            progressReporter.Report(new CreateVMProgressInfo
            {
                URI = finalUri,
                DownloadSpeed = 0,
                ProgressPercentage = 100
            });
        }
    }
}
