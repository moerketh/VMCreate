using System.Threading.Tasks;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Strategy that knows how to create and customize a specific kind of VM image.
    /// The dispatcher selects a strategy based on the source file type and gallery metadata.
    /// </summary>
    public interface IVmCreationStrategy
    {
        /// <summary>
        /// Returns true if this strategy can handle the supplied gallery item and actual file type.
        /// </summary>
        bool CanHandle(GalleryItem item, string actualFileType);

        /// <summary>
        /// Creates the VM described by the context. Implementations report progress,
        /// observe cancellation, and throw on fatal errors.
        /// </summary>
        Task CreateVMAsync(VmCreationContext context);
    }
}
