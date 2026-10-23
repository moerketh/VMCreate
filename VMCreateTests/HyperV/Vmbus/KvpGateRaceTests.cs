using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;
using KvpGateRace = CreateVM.HyperV.vmbus.KvpGateRace;

namespace VMCreate.Tests.HyperV.Vmbus
{
    /// <summary>
    /// Race semantics of <see cref="CreateVM.HyperV.vmbus.KvpGateRace"/>:
    /// early witness exit, gate cancellation, budget preservation and
    /// cancellation propagation — all delegate-injected, no Hyper-V.
    /// </summary>
    [TestClass]
    public class KvpGateRaceTests
    {
        private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(50);

        [TestMethod]
        public async Task PoolConfirmDuringGrace_SkipsTheWitness()
        {
            bool witnessRan = false;
            var outcome = await KvpGateRace.RunAsync(
                _ => Task.FromResult(true),
                _ => { witnessRan = true; return Task.FromResult(true); },
                Grace, CancellationToken.None);

            Assert.AreEqual(KvpGateRace.Outcome.PoolConfirmed, outcome);
            Assert.IsFalse(witnessRan, "the witness must not run when pools confirm within grace");
        }

        [TestMethod]
        public async Task SilentPoolsWithLiveDaemon_WitnessConfirmsEarly()
        {
            // The observed live case: pools unreadable for the entire budget
            // while the guest daemon answers. The race must return early at
            // grace instead of waiting out the (simulated long) gate.
            var gateStartedAt = DateTime.UtcNow;
            var outcome = await KvpGateRace.RunAsync(
                async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return false; },
                _ => Task.FromResult(true),
                Grace, CancellationToken.None);
            var elapsed = DateTime.UtcNow - gateStartedAt;

            Assert.AreEqual(KvpGateRace.Outcome.WitnessConfirmed, outcome);
            Assert.IsTrue(elapsed < TimeSpan.FromSeconds(10),
                $"witness confirmation must not wait out the gate budget (took {elapsed.TotalSeconds:F1}s)");
        }

        [TestMethod]
        public async Task SilentPoolsSilentWitness_GateRunsFullBudget_TimesOut()
        {
            // The failure path is unchanged: a silent witness leaves the
            // verdict to the gate, which times out.
            var outcome = await KvpGateRace.RunAsync(
                _ => Task.FromResult(false),
                _ => Task.FromResult(false),
                Grace, CancellationToken.None);

            Assert.AreEqual(KvpGateRace.Outcome.TimedOut, outcome);
        }

        [TestMethod]
        public async Task WitnessConfirmed_CancelsTheGate()
        {
            CancellationToken gateToken = CancellationToken.None;
            var gateCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var witnessAllowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var race = KvpGateRace.RunAsync(
                async ct =>
                {
                    gateToken = ct;
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { gateCompletion.SetCanceled(); }
                    return false;
                },
                async _ => { await witnessAllowed.Task; return true; },
                Grace, CancellationToken.None);

            await Task.Delay(Grace + Grace, CancellationToken.None); // let grace elapse, witness start
            witnessAllowed.SetResult();
            Assert.AreEqual(KvpGateRace.Outcome.WitnessConfirmed, await race);
            Assert.IsTrue(gateToken.IsCancellationRequested, "early exit must cancel the gate's polling");
        }

        [TestMethod]
        public async Task OuterCancellation_Propagates()
        {
            // Grace elapses first; the witness then blocks on the token, so
            // the race can only end via cancellation — proving the outer
            // token flows through the grace delay AND the witness call.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => KvpGateRace.RunAsync(
                    async ct => { await Task.Delay(Timeout.Infinite, ct); return false; },
                    async ct => { await Task.Delay(Timeout.Infinite, ct); return false; },
                    Grace, cts.Token));
        }
    }
}
