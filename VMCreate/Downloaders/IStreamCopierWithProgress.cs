using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Copies one stream to another while reporting progress and optional download metadata.
    /// </summary>
    public interface IStreamCopierWithProgress
    {
        Task CopyAsync(
            Stream source,
            Stream destination,
            long? totalBytes,
            string uri,
            IProgress<CreateVMProgressInfo> progress,
            CancellationToken cancellationToken);
    }
}
