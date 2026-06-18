using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    public class StreamCopierWithProgress : IStreamCopierWithProgress
    {
        private const int BufferSize = 65536;
        private const int UpdateIntervalMs = 1000;
        private readonly ILogger<StreamCopierWithProgress> _logger;

        public StreamCopierWithProgress(ILogger<StreamCopierWithProgress> logger = null)
        {
            _logger = logger;
        }

        public async Task CopyAsync(
            Stream source,
            Stream destination,
            long? totalBytes,
            string uri,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            long totalBytesRead = 0;
            byte[] buffer = new byte[BufferSize];
            int bytesRead;
            DateTime lastUpdate = DateTime.Now;
            long lastBytesRead = 0;

            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalBytesRead += bytesRead;

                if ((DateTime.Now - lastUpdate).TotalMilliseconds >= UpdateIntervalMs)
                {
                    double elapsedSeconds = (DateTime.Now - lastUpdate).TotalSeconds;
                    double speedMBps = elapsedSeconds > 0
                        ? (totalBytesRead - lastBytesRead) / elapsedSeconds / 1024 / 1024
                        : 0;

                    progress?.Report(new CreateVMProgressInfo
                    {
                        Phase = VmDeploymentPhase.Download,
                        ProgressPercentage = totalBytes.HasValue && totalBytes.Value > 0
                            ? (int)((totalBytesRead * 100) / totalBytes.Value)
                            : 0,
                        DownloadSpeed = speedMBps
                    });

                    _logger?.LogDebug(
                        "Downloaded {BytesRead} bytes ({Percent}% complete) at {Speed:F2} MB/s for {Uri}",
                        totalBytesRead,
                        totalBytes.HasValue && totalBytes.Value > 0
                            ? (totalBytesRead * 100) / totalBytes.Value
                            : 0,
                        speedMBps,
                        uri);

                    lastUpdate = DateTime.Now;
                    lastBytesRead = totalBytesRead;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            progress?.Report(new CreateVMProgressInfo
            {
                Phase = VmDeploymentPhase.Download,
                ProgressPercentage = totalBytes.HasValue && totalBytes.Value > 0 ? 100 : 0,
                URI = uri
            });
        }
    }
}
