using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    /// <summary>
    /// Collects diagnostic information from a guest VM when customization stalls or fails.
    /// </summary>
    public interface IGuestDiagnosticsCollector
    {
        /// <summary>
        /// Connects to the ISO guest and collects autorun service status, journal output,
        /// mount state, and recent kernel messages.
        /// </summary>
        Task<GuestDiagnostics> CollectAsync(string vmName, CancellationToken ct, string privateKeyPath = null);
    }
}
