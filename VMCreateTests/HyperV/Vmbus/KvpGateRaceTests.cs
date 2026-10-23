using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;
using KvpGateRace = CreateVM.HyperV.vmbus.KvpGateRace;

namespace VMCreate.Tests.HyperV.Vmbus
{
    /// <summary>
    /// Race semantics of <see cref="CreateVM.HyperV.vmbus.KvpGateRace"/>:
    /// witness-first early exit, pool-wins precedence, gate cancellation,
    /// budget preservation and cancellation propagation — all
    /// delegate-injected, no Hyper-V.
    /// </summary>
    [TestClass]
    public class KvpGateRaceTests
    {
        private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(50);

        [TestMethod]
        public async Task LiveDaemon_WitnessConfirmsImmediately_WithoutWaitingGrace()
        {
            // The observed live case (first boot): pools unreadable for the
            // whole budget while the guest daemon answers. The first witness
            // probe runs at t=0 — confirmation must not pay the grace.
            var startedAt = DateTime.UtcNow;
            var outcome = await KvpGateRace.RunAsync(
                async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return false; },
                _ => Task.FromResult(true),
                Grace, CancellationToken.None);
            var elapsed = DateTime.UtcNow - startedAt;

            Assert.AreEqual(KvpGateRace.Outcome.WitnessConfirmed, outcome);
            Assert.IsTrue(elapsed < Grace,
                $"an immediately-confirmed daemon must proceed without the grace (took {elapsed.TotalMilliseconds:F0}ms)");
        }

        [TestMethod]
        public async Task PoolConfirm_WinsOverSlowWitness()
        {
            // Pools settle while the witness probe is still in flight: the
            // pool verdict is the certain one and must win; the unobserved
            // witness result must not fault the race.
            int witnessCalls = 0;
            var outcome = await KvpGateRace.RunAsync(
                _ => Task.FromResult(true),
                async _ => { witnessCalls++; await Task.Delay(TimeSpan.FromSeconds(30)); return true; },
                Grace, CancellationToken.None);

            Assert.AreEqual(KvpGateRace.Outcome.PoolConfirmed, outcome);
            Assert.AreEqual(1, witnessCalls, "the immediate probe ran; no re-probe is needed after a pool confirm");
        }

        [TestMethod]
        public async Task SilentPoolsSilentWitness_GateRunsFullBudget_TimesOut()
        {
            // The failure path is unchanged: a silent witness (immediate AND
            // post-grace) leaves the verdict to the gate, which times out.
            var outcome = await KvpGateRace.RunAsync(
                _ => Task.FromResult(false),
                _ => Task.FromResult(false),
                Grace, CancellationToken.None);

            Assert.AreEqual(KvpGateRace.Outcome.TimedOut, outcome);
        }

        [TestMethod]
        public async Task SilentFirstWitness_DelayedDaemonConfirmsAfterGrace()
        {
            // Daemon still starting at t=0 (first probe silent), answering by
            // the post-grace re-probe: proceeds early instead of timing out.
            int calls = 0;
            var outcome = await KvpGateRace.RunAsync(
                async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return false; },
                _ => Task.FromResult(++calls == 2),
                Grace, CancellationToken.None);

            Assert.AreEqual(KvpGateRace.Outcome.WitnessConfirmed, outcome);
            Assert.AreEqual(2, calls, "the delayed daemon is caught by the post-grace re-probe");
        }

        [TestMethod]
        public async Task WitnessConfirmed_CancelsTheGate()
        {
            CancellationToken gateToken = CancellationToken.None;
            var gateCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var race = KvpGateRace.RunAsync(
                async ct =>
                {
                    gateToken = ct;
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { gateCompletion.SetCanceled(); }
                    return false;
                },
                _ => Task.FromResult(true),
                Grace, CancellationToken.None);

            Assert.AreEqual(KvpGateRace.Outcome.WitnessConfirmed, await race);
            Assert.IsTrue(gateToken.IsCancellationRequested, "early exit must cancel the gate's polling");
            await Task.Delay(Grace + Grace); // let the gate observe its cancellation
        }

        [TestMethod]
        public async Task OuterCancellation_Propagates()
        {
            // Both probes block on the token; the immediate witness probe at
            // t=0 means cancellation must flow through it without the grace.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => KvpGateRace.RunAsync(
                    async ct => { await Task.Delay(Timeout.Infinite, ct); return false; },
                    async ct => { await Task.Delay(Timeout.Infinite, ct); return false; },
                    Grace, cts.Token));
        }
    }
}
