using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.MediaHandlers
{
    public interface IMediaHandler
    {
        bool RequiresExtraction { get; }
        int VmGeneration { get; }
        long DetectedVirtualSizeBytes { get; }
        Task<string> PrepareMediaAsync(string sourceFile, string destinationPath, VmSettings vmSettings, GalleryItem item, IProgress<CreateVMProgressInfo> progressInfo, CancellationToken cancellationToken);
    }
}