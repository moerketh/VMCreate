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
    /// <para>
    /// A <c>false</c> return is "unconfirmed", NOT "dead": on at least one
    /// 2025-era host the pools materialized ~19 minutes after first boot while
    /// the guest daemon answered within seconds of SSH. Callers must
    /// disambiguate with a guest-side witness probe before failing. The
    /// default timeout leaves room for slow pool materialization; a longer
    /// call-site wait must pass <paramref name="timeoutSeconds"/> explicitly.
    /// </para>
    /// </summary>
    public interface IKvpHeartbeatGate
    {
        /// <summary>
        /// What the most recent <see cref="WaitForGuestKvpAsync"/> wait
        /// observed, or null when this gate has not run (or its implementation
        /// does not track). Embedded by callers into timeout diagnostics.
        /// </summary>
        KvpGateDiagnostics? LastDiagnostics { get; }

        /// <summary>
        /// Polls the guest-written KVP pools until the guest daemon's output is
        /// visible on the host, proving it is running and answering.
        /// </summary>
        /// <returns><c>true</c> when the heartbeat was confirmed; <c>false</c> on timeout — including a host-side pool that simply has not materialized yet.</returns>
        Task<bool> WaitForGuestKvpAsync(
            string vmName,
            CancellationToken cancellationToken,
            int timeoutSeconds = 420,
            int pollIntervalMs = 2000);
    }
}
