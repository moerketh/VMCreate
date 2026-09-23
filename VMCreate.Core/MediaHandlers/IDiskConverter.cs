using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Converts disk images between formats (VMDK/QCOW2 → VHDX) using qemu-img.
    /// </summary>
    public interface IDiskConverter
    {
        /// <summary>
        /// Converts a source disk image to VHDX format, including de-sparsification.
        /// </summary>
        Task<string> ConvertToVhdxAsync(string sourcePath, string destinationPath, IProgress<CreateVMProgressInfo> progress, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the virtual size (in bytes) of a disk image using qemu-img info.
        /// </summary>
        Task<long> GetVirtualSizeAsync(string diskPath, CancellationToken cancellationToken = default);
    }
}
