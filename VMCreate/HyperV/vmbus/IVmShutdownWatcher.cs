using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    /// <summary>
    /// Monitors VM lifecycle state (running/shutdown) via WMI.
    /// </summary>
    public interface IVmShutdownWatcher
    {
        /// <summary>
        /// Poll until VM is running (EnabledState = 2) and return GUID.
        /// </summary>
        Task<string> WaitForVMRunningAsync(string vmName, CancellationToken cancellationToken, int timeoutSeconds = 300, int pollIntervalMs = 1000);

        /// <summary>
        /// Wait for VM to shut down. Returns true if the VM shut down, false if the timeout expired.
        /// Set timeoutSeconds=0 for no timeout.
        /// </summary>
        Task<bool> WaitForVMShutdownAsync(string vmName, CancellationToken cancellationToken, int timeoutSeconds, int pollIntervalMs = 1000);
    }
}
