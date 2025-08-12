using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    public interface IStreamCopierWithProgress
    {
        Task CopyAsync(Stream source, Stream destination, long? contentLength, string finalUri, IProgress<CreateVMProgressInfo> progressReportInfo, CancellationToken cancellationToken);
    }

    public class StreamCopierWithProgress : StreamCopier, IStreamCopierWithProgress
    {
        private long? _contentLength;
        private string _finalUri;
        private IProgress<CreateVMProgressInfo> _progressReportInfo;
        private readonly ILogger _logger;

        public StreamCopierWithProgress()
        {}

        public StreamCopierWithProgress(ILogger<StreamCopier> logger) : base(logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public virtual async Task CopyAsync(Stream source, Stream destination, long? contentLength, string finalUri, IProgress<CreateVMProgressInfo> progressReportInfo, CancellationToken cancellationToken)
        {
            _contentLength = contentLength;
            _finalUri = finalUri;
            _progressReportInfo = progressReportInfo;

            await CopyAsync(source, destination, cancellationToken);
        }

        protected override void OnStart()
        {
            _progressReportInfo.Report(new CreateVMProgressInfo
            {
                URI = _finalUri,
                DownloadSpeed = 0,
                ProgressPercentage = 0
            });
        }

        protected override void OnProgressUpdate(double speed, long totalBytesRead)
        {
            int percentage = _contentLength.HasValue ? Convert.ToInt32((double)totalBytesRead / _contentLength.Value * 100) : 0;

            _progressReportInfo.Report(new CreateVMProgressInfo
            {
                URI = _finalUri,
                DownloadSpeed = speed,
                ProgressPercentage = percentage
            });
        }

        protected override void OnComplete(long totalBytesRead)
        {
            _progressReportInfo.Report(new CreateVMProgressInfo
            {
                URI = _finalUri,
                DownloadSpeed = 0,
                ProgressPercentage = 100
            });
        }
    }
}
