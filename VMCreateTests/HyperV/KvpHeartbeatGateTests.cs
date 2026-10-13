using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using CreateVM.HyperV.vmbus;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VMCreate.Tests.HyperV
{
    /// <summary>
    /// Tests for the KVP heartbeat gate: the guard that fails the pipeline
    /// loudly when the guest's integration daemon is installed but not
    /// answering (the quickspawn "agent unstarted" bug class).
    /// </summary>
    [TestClass]
    public sealed class KvpHeartbeatGateTests
    {
        private sealed class FakeGate : KvpHeartbeatGate
        {
            private readonly Func<int, string?> _guidReader;
            private readonly Func<int, Dictionary<string, string?>> _kvpReader;
            public int PollCount { get; private set; }

            public FakeGate(Func<int, string?> guidReader, Func<int, Dictionary<string, string?>> kvpReader)
            {
                _guidReader = guidReader;
                _kvpReader = kvpReader;
            }

            protected override string? GetVMGuid(string vmName)
            {
                var guid = _guidReader(PollCount);
                return guid;
            }

            protected override Dictionary<string, string?> ReadGuestKvpItems(string vmGuid)
                => _kvpReader(PollCount++);

            public Task<bool> WaitAsync(int timeoutSeconds, int pollMs, CancellationToken ct)
                => WaitForGuestKvpAsync("TestVM", ct, timeoutSeconds, pollMs);
        }

        [TestMethod]
        public async Task GuestDaemonAnswering_ReturnsTrueImmediately()
        {
            var gate = new FakeGate(
                _ => "11111111-1111-1111-1111-111111111111",
                _ => new Dictionary<string, string?> { ["OSName"] = "Linux" });

            Assert.IsTrue(await gate.WaitAsync(timeoutSeconds: 5, pollMs: 1, CancellationToken.None));
            Assert.AreEqual(1, gate.PollCount, "answering daemon must not need retries");
        }

        [TestMethod]
        public async Task EmptyKvpPools_TimesOutReturnsFalse()
        {
            // Daemon installed but never started: VM runs, no guest-written
            // KVP data ever appears.
            var gate = new FakeGate(
                _ => "11111111-1111-1111-1111-111111111111",
                _ => new Dictionary<string, string?>());

            Assert.IsFalse(await gate.WaitAsync(timeoutSeconds: 1, pollMs: 1, CancellationToken.None),
                "empty pools after timeout must return false, not hang or throw");

            var diag = gate.LastDiagnostics;
            Assert.IsNotNull(diag, "a completed wait must leave diagnostics behind");
            Assert.IsTrue(diag.VmGuidResolved, "the VM was running and locatable");
            Assert.IsTrue(diag.PoolReadCount >= 1, "pools were readable");
            Assert.IsFalse(diag.ConfirmedGuestKvp, "no guest-written data was seen");
            Assert.IsNull(diag.LastException, "no WMI errors occurred");
            StringAssert.Contains(diag.ToString(), "pools read empty", StringComparison.Ordinal,
                "timeout diagnostics must name the host-side lag signature");
        }

        [TestMethod]
        public async Task DaemonStartsLate_ReturnsTrue()
        {
            // First two polls see nothing (daemon still starting), third
            // poll sees guest-written data — the image quirk resolving itself.
            var gate = new FakeGate(
                _ => "11111111-1111-1111-1111-111111111111",
                poll => poll < 2
                    ? new Dictionary<string, string?>()
                    : new Dictionary<string, string?> { ["C31CA605-9DCF-4718-A556-9ECAFA279F79"] = "heartbeat" });

            Assert.IsTrue(await gate.WaitAsync(timeoutSeconds: 10, pollMs: 1, CancellationToken.None));
            Assert.AreEqual(3, gate.PollCount, "must keep polling until the daemon answers");
        }

        [TestMethod]
        public async Task VmNotRunning_TimesOutReturnsFalse()
        {
            var gate = new FakeGate(_ => null, _ => new Dictionary<string, string?>());

            Assert.IsFalse(await gate.WaitAsync(timeoutSeconds: 1, pollMs: 1, CancellationToken.None));

            var diag = gate.LastDiagnostics;
            Assert.IsNotNull(diag);
            Assert.IsFalse(diag.VmGuidResolved, "the VM never became locatable");
            Assert.AreEqual(0, diag.PoolReadCount, "no pool read can succeed without a GUID");
            StringAssert.Contains(diag.ToString(), "VM GUID never resolved", StringComparison.Ordinal,
                "diagnostics must distinguish 'VM not locatable' from 'pools empty'");
        }

        [TestMethod]
        public async Task TransientWmiFailure_KeepsPolling()
        {
            var gate = new FakeGate(
                _ => "11111111-1111-1111-1111-111111111111",
                poll => poll < 1
                    ? throw new ManagementException("transient")
                    : new Dictionary<string, string?> { ["OSName"] = "Linux" });

            Assert.IsTrue(await gate.WaitAsync(timeoutSeconds: 10, pollMs: 1, CancellationToken.None),
                "a transient WMI error must not fail the gate");

            var diag = gate.LastDiagnostics;
            Assert.IsNotNull(diag);
            Assert.IsTrue(diag.ConfirmedGuestKvp, "the wait ended in a confirmed heartbeat");
            Assert.AreEqual("transient", diag.LastException,
                "the swallowed WMI error must be recorded for post-mortem diagnostics");
            StringAssert.Contains(diag.ToString(), "guest KVP confirmed", StringComparison.Ordinal);
        }

        [TestMethod]
        public async Task ConfirmedHeartbeat_DiagnosticsShowConfirmation()
        {
            var gate = new FakeGate(
                _ => "11111111-1111-1111-1111-111111111111",
                _ => new Dictionary<string, string?> { ["OSName"] = "Linux" });

            Assert.IsTrue(await gate.WaitAsync(timeoutSeconds: 5, pollMs: 1, CancellationToken.None));

            var diag = gate.LastDiagnostics;
            Assert.IsNotNull(diag);
            Assert.IsTrue(diag.ConfirmedGuestKvp);
            Assert.AreEqual(1, diag.PollCount, "an answering daemon confirms on the first poll");
            Assert.IsNull(diag.LastException);
        }

        [TestMethod]
        public async Task EachWait_ResetsDiagnostics()
        {
            bool daemonAlive = false;
            var gate = new FakeGate(
                _ => "11111111-1111-1111-1111-111111111111",
                _ => daemonAlive
                    ? new Dictionary<string, string?> { ["OSName"] = "Linux" }
                    : new Dictionary<string, string?>());

            Assert.IsFalse(await gate.WaitAsync(timeoutSeconds: 1, pollMs: 1, CancellationToken.None));
            var firstWait = gate.LastDiagnostics;
            Assert.IsNotNull(firstWait);
            Assert.IsTrue(firstWait.PollCount > 1, "the timed-out wait polled repeatedly");
            Assert.IsFalse(firstWait.ConfirmedGuestKvp);

            daemonAlive = true;
            Assert.IsTrue(await gate.WaitAsync(timeoutSeconds: 5, pollMs: 1, CancellationToken.None));
            var secondWait = gate.LastDiagnostics;
            Assert.IsNotNull(secondWait);
            Assert.AreNotSame(firstWait, secondWait, "each wait must produce fresh diagnostics");
            Assert.AreEqual(1, secondWait.PollCount,
                "the fresh wait's poll counter must start from zero, not accumulate");
            Assert.IsTrue(secondWait.ConfirmedGuestKvp);
        }

        [TestMethod]
        public async Task CancelledToken_StopsPolling()
        {
            var gate = new FakeGate(
                _ => "11111111-1111-1111-1111-111111111111",
                _ => new Dictionary<string, string?>());

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => gate.WaitAsync(timeoutSeconds: 10, pollMs: 1, cts.Token));
        }

        [TestMethod]
        public async Task InvalidVmName_ThrowsImmediately()
        {
            var gate = new KvpHeartbeatGate();

            await Assert.ThrowsAsync<ArgumentException>(
                () => gate.WaitForGuestKvpAsync("bad'name", CancellationToken.None));
        }
    }
}
