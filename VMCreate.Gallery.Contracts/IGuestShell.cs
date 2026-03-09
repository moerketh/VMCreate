using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Abstraction for executing commands and transferring files inside a guest VM.
    /// Implementations handle the transport details (SSH, PowerShell Direct, etc.)
    /// so that customization steps remain transport-agnostic and testable.
    /// </summary>
    public interface IGuestShell
    {
        /// <summary>The display name of the VM this shell is connected to.</summary>
        string VmName { get; }

        /// <summary>
        /// Executes a bash command on the guest and returns stdout.
        /// Throws on non-zero exit code or transport failure.
        /// </summary>
        Task<string> RunCommandAsync(string bashCommand, CancellationToken ct);

        /// <summary>
        /// Writes string content to a file on the guest (creates parent directories, sets 644).
        /// Used for API-downloaded configs that exist only in memory.
        /// </summary>
        Task CopyContentAsync(string content, string guestPath, CancellationToken ct);

        /// <summary>
        /// Copies a host file to the guest (creates parent directories, sets 644).
        /// </summary>
        Task CopyFileAsync(string hostPath, string guestPath, CancellationToken ct);

        /// <summary>
        /// Waits until the guest VM is reachable and the shell is ready for commands.
        /// Transport-specific implementations may poll via SSH, KVP, etc.
        /// </summary>
        Task WaitForReadyAsync(CancellationToken ct);
    }
}
