using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Monitors KVP-based progress from a guest VM and reports shutdown status.
    /// </summary>
    public interface IKvpPoller
    {
        /// <summary>
        /// Polls KVP for partclone progress and reports to the provided progress reporter.
        /// </summary>
        Task PollKVPForProgressAsync(string vmName, IProgress<CreateVMProgressInfo> progressReporter, CancellationToken cancellationToken);

        /// <summary>
        /// Polls WorkflowProgress KVP while waiting for VM shutdown.
        /// Returns true if the VM shut down cleanly, false on timeout.
        /// </summary>
        Task<bool> WaitForShutdownWithProgressAsync(string vmName, IProgress<CreateVMProgressInfo> progressReporter, CancellationToken cancellationToken, int timeoutSeconds = 600);
    }
}
