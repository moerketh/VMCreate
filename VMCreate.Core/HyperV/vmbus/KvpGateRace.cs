using System;
using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    /// <summary>
    /// Races the KVP pool gate against the guest-side witness probe.
    /// </summary>
    /// <remarks>
    /// The pool gate alone can burn its entire budget on healthy guests:
    /// the host-side KVP pools can lag the guest daemon by many minutes on
    /// first boot (observed live: 421s of WMI "Not found" polls while the
    /// guest's daemon had been answering SSH the whole time). The witness
    /// therefore probes the guest FIRST — an immediately confirmed daemon
    /// proceeds at once. A silent witness (daemon not up yet) falls back to
    /// the pool gate with a post-grace re-probe; the gate then runs its
    /// full budget and the caller's failure path decides. Pure and
    /// delegate-injected so the race semantics (early exit, gate
    /// cancellation, budget preservation, cancellation propagation) are
    /// unit-testable without Hyper-V.
    /// </remarks>
    internal static class KvpGateRace
    {
        internal enum Outcome
        {
            /// <summary>Pools populated — the fast, certain path.</summary>
            PoolConfirmed,

            /// <summary>Pools unreadable, guest daemon confirmed by the
            /// witness — proceed early.</summary>
            WitnessConfirmed,

            /// <summary>Pools never populated within the gate budget and the
            /// witness did not confirm — caller runs its failure path.</summary>
            TimedOut,
        }

        /// <param name="startGate">Starts the pool gate with the given token
        /// (the race cancels it on an early witness exit).</param>
        /// <param name="witnessProbe">Asks the guest over SSH whether its KVP
        /// daemon runs; true = confirmed.</param>
        /// <param name="grace">How long the gate polls before the witness is
        /// re-probed (the first probe happens immediately).</param>
        internal static async Task<Outcome> RunAsync(
            Func<CancellationToken, Task<bool>> startGate,
            Func<CancellationToken, Task<bool>> witnessProbe,
            TimeSpan grace,
            CancellationToken cancellationToken)
        {
            using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<bool> gateTask = startGate(gateCts.Token);
            // A gate canceled (or faulted) after an early exit must not go
            // unobserved: park a no-op observer on it.
            _ = gateTask.ContinueWith(
                static t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted);

            // Witness-first: the daemon answers SSH within seconds of boot,
            // while pools lag by minutes — an immediate confirmation is the
            // common healthy case and must not pay the grace at all. When
            // pools confirm in parallel, that verdict wins (most certain).
            Task<bool> witnessTask = witnessProbe(cancellationToken);
            Task<bool> firstDone = await Task.WhenAny(gateTask, witnessTask).ConfigureAwait(false);
            if (firstDone == gateTask)
            {
                // Pools settled before the witness answered: their verdict
                // needs no witness; the in-flight probe must never go
                // unobserved if it faults later.
                _ = witnessTask.ContinueWith(
                    static t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);
                return await gateTask.ConfigureAwait(false) ? Outcome.PoolConfirmed : Outcome.TimedOut;
            }

            if (await witnessTask.ConfigureAwait(false))
            {
                gateCts.Cancel();
                return Outcome.WitnessConfirmed;
            }

            // First witness silent (daemon not up yet): race the gate to the
            // grace mark, then re-probe once.
            if (await Task.WhenAny(gateTask, Task.Delay(grace, cancellationToken)).ConfigureAwait(false) == gateTask)
                return await gateTask.ConfigureAwait(false) ? Outcome.PoolConfirmed : Outcome.TimedOut;

            if (await witnessProbe(cancellationToken).ConfigureAwait(false))
            {
                gateCts.Cancel();
                return Outcome.WitnessConfirmed;
            }

            // Witness silent — the gate decides over its full budget.
            return await gateTask.ConfigureAwait(false) ? Outcome.PoolConfirmed : Outcome.TimedOut;
        }
    }
}
