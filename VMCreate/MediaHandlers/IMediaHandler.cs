using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.MediaHandlers
{
    public interface IMediaHandler
    {
        /// <summary>
        /// Canonical file type this handler represents (ISO, VHDX, VMDK, QCOW2).
        /// </summary>
        DiskImageFormat FileType { get; }

        bool RequiresExtraction { get; }
        int VmGeneration { get; }
        long DetectedVirtualSizeBytes { get; }
        Task<MediaPreparationResult> PrepareMediaAsync(
            string sourceFile,
            string destinationPath,
            VmDeploymentPlan plan,
            GalleryItem item,
            IProgress<CreateVMProgressInfo> progressInfo,
            CancellationToken cancellationToken);
    }
}
