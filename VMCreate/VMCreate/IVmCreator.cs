using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Entry-point contract for VM creation. Implementers accept an immutable
    /// <see cref="VmDeploymentPlan"/> (which already includes the effective VM name)
    /// and coordinate media preparation, Hyper-V provisioning, and cleanup.
    /// </summary>
    public interface IVmCreator
    {
        /// <summary>
        /// Deploys the VM described by <paramref name="plan"/>.
        /// </summary>
        /// <param name="plan">Immutable deployment plan, normally constructed by the caller from wizard settings.</param>
        /// <param name="vmCustomizations">Customization values collected from the wizard.</param>
        /// <param name="galleryItem">Selected gallery distribution.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="progress">Progress reporter.</param>
        /// <param name="sourceFile">Optional local disk file after download/extraction; falls back to <see cref="GalleryItem.DiskUri"/>.</param>
        /// <returns>
        /// A <see cref="VmDeploymentResult"/> describing the effective VM name and success/failure state.
        /// On failure, cleanup has already been attempted and the exception is re-thrown.
        /// </returns>
        Task<VmDeploymentResult> CreateAsync(
            VmDeploymentPlan plan,
            VmCustomizations vmCustomizations,
            GalleryItem galleryItem,
            CancellationToken cancellationToken = default,
            IProgress<CreateVMProgressInfo> progress = null,
            string sourceFile = null);
    }
}
