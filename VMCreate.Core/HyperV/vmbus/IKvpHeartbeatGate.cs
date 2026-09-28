using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    /// <summary>
    /// Verifies that the guest's KVP daemon is actually answering before the
    /// pipeline proceeds. Guards against the "agent installed but not started"
    /// image quirk: a package can be present on disk while the daemon never
    /// runs, leaving later integration (KVP exchange, Enhanced Session)
    /// silently dead.
    /// </summary>
    public interface IKvpHeartbeatGate
    {
        /// <summary>
        /// Polls the guest-written KVP pools until the guest daemon's output is
        /// visible on the host, proving it is running and answering.
        /// </summary>
        /// <returns><c>true</c> when the heartbeat was confirmed; <c>false</c> on timeout.</returns>
        Task<bool> WaitForGuestKvpAsync(
            string vmName,
            CancellationToken cancellationToken,
            int timeoutSeconds = 120,
            int pollIntervalMs = 2000);
    }
}
