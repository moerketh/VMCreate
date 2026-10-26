using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using VMCreate.HyperV;

namespace VMCreate.Tests.HyperV
{
    /// <summary>
    /// Pins the pinned-IP SSH mode (CLI --static-ip): a statically
    /// configured guest may never appear in Hyper-V's DHCP-lease-driven IP
    /// discovery, so the shell connects to the user-guaranteed address
    /// directly — never discovering, never re-discovering.
    /// </summary>
    [TestClass]
    public sealed class SshGuestShellPinnedIpTests
    {
        private const string StaticIp = "192.168.10.50";

        /// <summary>
        /// The factory with a no-op logger: PowerShellDirectGuestShellFactory is
        /// a concrete class with ctor dependencies, and this test never touches
        /// its CreateForWindows path.
        /// </summary>
        private static GuestShellFactory Factory() => new(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GuestShellFactory>.Instance,
            new PowerShellDirectGuestShellFactory(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PowerShellDirectGuestShellFactory>.Instance));

        [TestMethod]
        public void PinnedIpTarget_IsTheStaticAddress()
        {
            var shell = (SshGuestShell)Factory().CreateWithPinnedIp("TestVM", @"C:\k\id_ed25519", "omarchy", StaticIp);

            Assert.AreEqual(StaticIp, shell.CurrentTargetForTests.Host);
            Assert.AreEqual(SshConnectionTarget.TargetKind.IpAddress, shell.CurrentTargetForTests.Kind,
                "the pinned target is a plain IP target — reached over TCP, not vsock");
        }

        [TestMethod]
        public void PinnedIpShell_RequiresAnAddress()
        {
            Assert.Throws<ArgumentException>(() =>
                Factory().CreateWithPinnedIp("TestVM", @"C:\k\id_ed25519", "omarchy", "  "));
        }

        [TestMethod]
        public void PinnedIpShell_IsUsableBeforeWaitForReady()
        {
            // The fixed target exists from construction: execs before
            // WaitForReadyAsync work (unlike discovery mode, which throws
            // its actionable not-ready error until an IP is discovered).
            var shell = new SshGuestShell(
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                "TestVM", @"C:\k\id_ed25519",
                "omarchy", SshConnectionTarget.ForIp(StaticIp));

            Assert.AreEqual(StaticIp, shell.CurrentTargetForTests.Host);
        }

        [TestMethod]
        public void CreateWithPinnedIp_DistinctFromVsock_UsesTcpTarget()
        {
            var pinned = (SshGuestShell)Factory().CreateWithPinnedIp("TestVM", @"C:\k\id_ed25519", "vmcreate", StaticIp);
            Assert.AreEqual(0, pinned.CurrentTargetForTests.ExtraArguments.Count,
                "an IP target carries no ProxyCommand extras (those are vsock-only)");
        }

        [TestMethod]
        public async Task PinnedIp_TransportErrorRetriesAtSameAddress_HostTimeoutDoesNot()
        {
            // Mid-pipeline session drops (NetworkManager restarting for a VPN
            // plugin install) must retry at the SAME pinned address — the
            // address cannot change, so failing the deploy there would be a
            // regression against discovery mode. A host-side TIMEOUT keeps
            // discovery mode's discipline: never re-run a merely slow
            // command (guest-side side effects could duplicate).
            var shell = new PinnedRetryProbeShell(
                transportError: new InvalidOperationException("Connection reset by peer"));

            string result = await shell.RunCommandProbeAsync("echo ok", CancellationToken.None);
            Assert.AreEqual("ok", result);
            Assert.AreEqual(2, shell.ExecAttempts, "a transport error at a pinned address retries");

            var slow = new PinnedRetryProbeShell(transportError: new TimeoutException("command timed out"));
            await Assert.ThrowsAsync<TimeoutException>(() =>
                slow.RunCommandProbeAsync("echo ok", CancellationToken.None));
            Assert.AreEqual(1, slow.ExecAttempts, "a host-side timeout is never re-run (side-effect discipline)");
        }

        /// <summary>
        /// Drives <see cref="SshGuestShell.RunWithRetryAsync"/> without a
        /// network: execs fail with the injected transport error until the
        /// retry succeeds. The retry loop is private, so the probe goes
        /// through <c>RunCommandAsync</c> — whose implementation routes
        /// straight into it.
        /// </summary>
        private sealed class PinnedRetryProbeShell : SshGuestShell
        {
            private readonly Exception _transportError;

            public PinnedRetryProbeShell(Exception transportError)
                : base(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                    "TestVM", @"C:\k\id_ed25519",
                    "vmcreate", SshConnectionTarget.ForIp("192.168.10.50"))
            {
                _transportError = transportError;
            }

            public int ExecAttempts { get; private set; }

            public Task<string> RunCommandProbeAsync(string command, CancellationToken ct)
                => RunCommandAsync(command, ct);

            protected override Task<string> RunCommandInternalAsync(string linuxCommand, TimeSpan timeout, CancellationToken ct)
            {
                ExecAttempts++;
                if (ExecAttempts == 1)
                    throw _transportError;
                return Task.FromResult("ok");
            }
        }
    }
}