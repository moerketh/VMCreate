using System;
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
        /// Executes a command on the guest and returns stdout.
        /// The command language is transport-defined: bash for SSH shells,
        /// PowerShell for PowerShell Direct. Throws on non-zero exit code or
        /// transport failure.
        /// </summary>
        Task<string> RunCommandAsync(string command, CancellationToken ct);

        /// <summary>
        /// Executes a command on the guest with an explicit timeout,
        /// for long-running steps (package installs, builds) that exceed the
        /// transport default. Throws on non-zero exit code, timeout, or
        /// transport failure.
        /// </summary>
        Task<string> RunCommandAsync(string command, TimeSpan timeout, CancellationToken ct);

        /// <summary>
        /// Writes string content to a file on the guest (creates parent directories, sets 644).
        /// Used for API-downloaded configs that exist only in memory.
        /// </summary>
        Task CopyContentAsync(string content, string guestPath, CancellationToken ct);

        /// <summary>
        /// Writes SECRET string content (private keys, VPN configs embedding
        /// client certificates, credentials) to a file on the guest. The
        /// result is owned by root:root with mode 0600 — never
        /// world-readable, and not group/other-readable by any other local
        /// account. Implementations must not place the material in
        /// intermediate world-readable locations along the way.
        /// </summary>
        Task CopySecretAsync(string content, string guestPath, CancellationToken ct);

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
