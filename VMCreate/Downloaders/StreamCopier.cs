using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    public class StreamCopier
    {
        protected const int BufferSize = 65536;
        protected const int UpdateIntervalMs = 1000;

        private readonly ILogger<StreamCopier> _logger;

        public StreamCopier(ILogger<StreamCopier> logger = null)
        {
            _logger = logger;
        }

        public virtual async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
        {
            OnStart();

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
                    double speed = (totalBytesRead - lastBytesRead) / elapsedSeconds / 1024 / 1024; // MB/s

                    OnProgressUpdate(speed, totalBytesRead);

                    lastUpdate = DateTime.Now;
                    lastBytesRead = totalBytesRead;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            OnComplete(totalBytesRead);
        }

        protected virtual void OnStart() { }

        protected virtual void OnProgressUpdate(double speed, long totalBytesRead) { }

        protected virtual void OnComplete(long totalBytesRead) { }
    }
}
