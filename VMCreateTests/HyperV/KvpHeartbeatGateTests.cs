using System;
using System.Collections.Generic;
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
