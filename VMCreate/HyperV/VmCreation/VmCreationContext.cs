using System;
using System.Threading;
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
            VmDeploymentPlan plan,
            VmCustomizations customizations,
            string sourceFile,
            GalleryItem galleryItem,
            MediaPreparationResult mediaResult,
            CancellationToken cancellationToken,
            IProgress<CreateVMProgressInfo> progress,
            IDeploymentLogger logger = null)
        {
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            Customizations = customizations ?? throw new ArgumentNullException(nameof(customizations));
            SourceFile = sourceFile ?? throw new ArgumentNullException(nameof(sourceFile));
            GalleryItem = galleryItem ?? throw new ArgumentNullException(nameof(galleryItem));
            MediaResult = mediaResult ?? throw new ArgumentNullException(nameof(mediaResult));
            CancellationToken = cancellationToken;
            Progress = progress ?? throw new ArgumentNullException(nameof(progress));
            Logger = logger ?? new DeploymentLogger(plan.VmName);
        }

        public VmDeploymentPlan Plan { get; }
        public VmCustomizations Customizations { get; }

        /// <summary>
        /// The original disk file passed to the creation pipeline (the downloaded
        /// or extracted file before media preparation moved/converted it).
        /// </summary>
        public string SourceFile { get; }

        public GalleryItem GalleryItem { get; }

        /// <summary>
        /// The canonical prepared media path plus discovered metadata. Callers
        /// should use <see cref="MediaPreparationResult.FinalMediaPath"/&gt; when
        /// attaching the disk to the VM.
        /// </summary>
        public MediaPreparationResult MediaResult { get; }

        public CancellationToken CancellationToken { get; }
        public IProgress<CreateVMProgressInfo> Progress { get; }

        /// <summary>
        /// Per-deployment log for the current VM. Always non-null.
        /// </summary>
        public IDeploymentLogger Logger { get; }
    }
}
