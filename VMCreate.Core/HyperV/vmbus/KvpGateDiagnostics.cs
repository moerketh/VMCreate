using System;

namespace CreateVM.HyperV.vmbus
{
    /// <summary>
    /// What <see cref="KvpHeartbeatGate"/> observed during its most recent
    /// <see cref="IKvpHeartbeatGate.WaitForGuestKvpAsync"/> wait. Deployment
    /// code embeds <see cref="ToString"/> in timeout errors so a failed gate
    /// can be diagnosed from the host side alone — in particular whether the
    /// host-side KVP pools merely stayed empty (a slow-to-materialize host
    /// artifact) versus the VM never being locatable at all.
    /// </summary>
    public sealed class KvpGateDiagnostics
    {
        public DateTime StartedUtc { get; internal set; }

        /// <summary>Number of poll iterations attempted (GUID lookup + read per poll).</summary>
        public int PollCount { get; internal set; }

        /// <summary>True when the VM's GUID resolved at least once (it is running and WMI can see it).</summary>
        public bool VmGuidResolved { get; internal set; }

        /// <summary>Number of successful KVP pool reads — even when every pool read empty.</summary>
        public int PoolReadCount { get; internal set; }

        /// <summary>True when the wait ended because a guest-written KVP entry was seen.</summary>
        public bool ConfirmedGuestKvp { get; internal set; }

        /// <summary>UTC timestamp of the last completed poll (null when no poll completed).</summary>
        public DateTime? LastPollUtc { get; internal set; }

        /// <summary>Message of the last WMI exception swallowed by the polling loop, if any.</summary>
        public string? LastException { get; internal set; }

        public override string ToString()
        {
            double elapsedSeconds = ((LastPollUtc ?? DateTime.UtcNow) - StartedUtc).TotalSeconds;
            string state = ConfirmedGuestKvp
                ? "guest KVP confirmed"
                : !VmGuidResolved
                    ? "VM GUID never resolved"
                    : PoolReadCount == 0
                        ? "pools never readable"
                        : "pools read empty";
            string detail =
                $"{PollCount} polls over {elapsedSeconds:F0}s, {PoolReadCount} empty pool reads" +
                (LastException == null ? "" : $", last WMI error: {LastException}");
            return $"{state} ({detail})";
        }
    }
}