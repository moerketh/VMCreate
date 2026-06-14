using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.MediaHandlers;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Context passed to a VM creation strategy. Contains everything required to create
    /// and customize a single VM from a prepared source file.
    /// </summary>
    public class VmCreationContext
    {
        public VmCreationContext(
            VmSettings settings,
            VmCustomizations customizations,
            string sourceFile,
            GalleryItem galleryItem,
            IMediaHandler mediaHandler,
            string mediaPath,
            CancellationToken cancellationToken,
            IProgress<CreateVMProgressInfo> progress)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Customizations = customizations ?? throw new ArgumentNullException(nameof(customizations));
            SourceFile = sourceFile ?? throw new ArgumentNullException(nameof(sourceFile));
            GalleryItem = galleryItem ?? throw new ArgumentNullException(nameof(galleryItem));
            MediaHandler = mediaHandler ?? throw new ArgumentNullException(nameof(mediaHandler));
            MediaPath = mediaPath ?? throw new ArgumentNullException(nameof(mediaPath));
            CancellationToken = cancellationToken;
            Progress = progress ?? throw new ArgumentNullException(nameof(progress));
        }

        public VmSettings Settings { get; }
        public VmCustomizations Customizations { get; }
        public string SourceFile { get; }
        public GalleryItem GalleryItem { get; }
        public IMediaHandler MediaHandler { get; }
        public string MediaPath { get; }
        public CancellationToken CancellationToken { get; }
        public IProgress<CreateVMProgressInfo> Progress { get; }
    }
}
