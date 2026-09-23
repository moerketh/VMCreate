using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.MediaHandlers
{
    /// <summary>
    /// Resolves the Hyper-V VM generation and validates drive size from a prepared disk image.
    /// Encapsulates partition-scheme detection, Gen1/MBR size validation, and auto-sizing so
    /// individual media handlers do not duplicate that logic.
    /// </summary>
    public interface IVmGenerationResolver
    {
        /// <summary>
        /// Inspects the supplied disk image and returns generation + size metadata.
        /// For generation 1 images, this also validates or auto-detects
        /// <see cref="VmDeploymentPlan.NewDriveSizeInGB"/>.
        /// </summary>
        Task<VmGenerationResolution> ResolveAsync(
            string diskImagePath,
            VmDeploymentPlan plan,
            CancellationToken cancellationToken);
    }
}
