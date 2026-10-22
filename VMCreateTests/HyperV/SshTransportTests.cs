using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Tests.HyperV
{
    /// <summary>
    /// Unit tests for the shared SSH transport (SshTransport) introduced with
    /// the SSH-runner dedup: both SshGuestShell and GuestDiagnosticsCollector
    /// delegate their exec here, and SshGuestShell additionally arms per-VM
    /// host-key TOFU pinning.
    /// </summary>
    [TestClass]
    public class SshTransportTests
    {
        private const string KeyPath = @"C:\tmp\vmcreate-test-key";
        private const string VmIp = "192.168.42.7";
        private const string User = "vmcreate";

        #region BuildArgumentList

        [TestMethod]
        public void BuildArgumentList_TofuMode_UsesAcceptNewAndPerVmKnownHosts()
        {
            string knownHosts = @"C:\Users\t\AppData\Local\Temp\VMCreate\known_hosts\test-vm.known_hosts";
            var args = SshTransport.BuildArgumentList(KeyPath, VmIp, User, knownHosts, "echo hi");

            AssertContainsPair(args, "-o", "StrictHostKeyChecking=accept-new");
            AssertContainsPair(args, "-o", $"UserKnownHostsFile={knownHosts}");
            Assert.IsFalse(args.Contains("StrictHostKeyChecking=no"), "TOFU mode must not disable host-key checking");
            Assert.IsFalse(args.Contains("UserKnownHostsFile=NUL"), "TOFU mode must not discard host keys");
        }

        [TestMethod]
        public void BuildArgumentList_NoPinningMode_UsesNoAndNul()
        {
            var args = SshTransport.BuildArgumentList(KeyPath, VmIp, User, null, "echo hi");

            AssertContainsPair(args, "-o", "StrictHostKeyChecking=no");
            AssertContainsPair(args, "-o", "UserKnownHostsFile=NUL");
            Assert.IsFalse(args.Contains("accept-new"), "Diagnostics mode must stay permissive (transient ISO guest)");
        }

        [TestMethod]
        public void BuildArgumentList_VsockTarget_UsesGuidHostAndAbsoluteHvcProxyCommand()
        {
            var target = SshConnectionTarget.ForVsock("d811e738-5c46-4f57-a171-4f822a38f22a");
            var args = SshTransport.BuildArgumentList(KeyPath, target, User, null, "echo hi");

            // Destination is the GUID, not an IP.
            StringAssert.Contains(string.Join(' ', args), $"{User}@d811e738-5c46-4f57-a171-4f822a38f22a");
            // ProxyCommand carries hvc by ABSOLUTE expanded path (anti-hijack,
            // same posture as SshExePath) and the vsock transport flag.
            string proxy = args.First(a => a.StartsWith("ProxyCommand=", StringComparison.Ordinal));
            StringAssert.Contains(proxy, "nc -t vsock %h 22");
            Assert.IsTrue(
                proxy.Contains(@"\System32\hvc.exe") || proxy.Contains("/System32/hvc.exe"),
                "ProxyCommand must invoke hvc.exe by absolute path");
            Assert.IsFalse(proxy.Contains("%SystemRoot%"), "ssh expands %-tokens in ProxyCommand itself; the path must arrive pre-expanded");
        }

        [TestMethod]
        public void BuildArgumentList_VsockTarget_NoPercentTokensExceptProxyHost()
        {
            // ssh performs %-token substitution inside ProxyCommand values:
            // any other % in the argv would be misread (e.g. %SystemRoot% as
            // %S + "ystemRoot%"). Assert the ONLY % in the whole element
            // list is the intended %h host token.
            var target = SshConnectionTarget.ForVsock("d811e738-5c46-4f57-a171-4f822a38f22a");
            var args = SshTransport.BuildArgumentList(KeyPath, target, User, null, "echo hi");

            int percentCount = string.Concat(args).Count(c => c == '%');
            Assert.AreEqual(1, percentCount, $"exactly one % (the %h token) expected, got {percentCount}: {string.Join(' ', args)}");
            StringAssert.Contains(string.Join(' ', args), "%h");
        }

        [TestMethod]
        public void BuildArgumentList_IpTarget_HasNoProxyCommand()
        {
            // The IP path must remain proxy-free: no vsock arguments leak
            // into post-boot connections.
            var args = SshTransport.BuildArgumentList(KeyPath, VmIp, User, null, "echo hi");
            Assert.IsTrue(args.All(a => !a.Contains("ProxyCommand", StringComparison.Ordinal)), "IP target must not carry a ProxyCommand");
        }

        /// <summary>
        /// The transport corrupts nothing when every element stays a whole
        /// argv element: option flags and their values must never be fused
        /// into one element (a fused "-o value" element would be parsed by
        /// ssh as a single unknown option).
        /// </summary>
        [TestMethod]
        public void BuildArgumentList_OptionsAreSeparateElements()
        {
            var args = SshTransport.BuildArgumentList(KeyPath, VmIp, User, null, "echo hi");

            AssertContainsPair(args, "-i", KeyPath);
            AssertContainsPair(args, "-o", "BatchMode=yes");
            AssertContainsPair(args, "-o", "ConnectTimeout=10");
            Assert.IsFalse(args.Any(a => a.StartsWith("-o ", StringComparison.Ordinal)), "option flags and values must be separate elements");
        }

        [TestMethod]
        public void BuildArgumentList_WrapsCommandInBashC()
        {
            var args = SshTransport.BuildArgumentList(KeyPath, VmIp, User, null, "echo 'ready'");
            // Payload "echo 'ready'" → 'echo '\''ready'\''' (open quote, escaped
            // inner quote pair, close quote): the whole command is ONE literal
            // shell word, as its own argv element right after bash -c.
            int bashC = Array.IndexOf(args.ToArray(), "bash");
            Assert.IsTrue(bashC >= 0 && args[bashC + 1] == "-c", "bash -c must lead the remote command");
            Assert.AreEqual("'echo '\\''ready'\\'''", args[bashC + 2],
                $"Expected single-quote-escaped payload element after bash -c, got: {string.Join(' ', args)}");
            Assert.AreEqual(args.Count - 3, bashC, "the payload must be the LAST element — nothing may follow it");
        }

        /// <summary>
        /// THE production regression: any double quote in a linuxCommand must
        /// reach the guest byte-for-byte. Hand-quoted raw Arguments strings
        /// lost every " to Windows argv parsing (then ssh.exe joined argv
        /// without re-quoting), which degraded the chroot sudo shim's
        /// <c>[ -n "$user" ]</c> to the always-true <c>[ -n $user ]</c> and
        /// killed deployments with "runuser: user -- does not exist". As its
        /// own ArgumentList element the payload cannot be touched.
        /// </summary>
        [TestMethod]
        public void BuildArgumentList_PayloadDoubleQuotesSurviveAsElement()
        {
            var args = SshTransport.BuildArgumentList(KeyPath, VmIp, User, null, "if [ -n \"$user\" ]; then echo hi; fi");

            string payload = args[^1];
            StringAssert.Contains(payload, "\"$user\"", "double quotes must survive verbatim inside the payload element");
            // The whole payload is still ONE element — no Windows quoting
            // may split it into several argv slots.
            Assert.AreEqual(1, args.Count(a => a.Contains("$user", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void BuildArgumentList_PayloadIsFullyEscaped()
        {
            // A quote-terminated command followed by a second command must be
            // impossible: the shell-safe single-quote escape makes the whole
            // payload one literal bash word.
            string malicious = "echo 'don''t'; rm -rf /";
            var args = SshTransport.BuildArgumentList(KeyPath, VmIp, User, null, malicious);

            // The raw payload must NOT appear as-is anywhere in the argv: the
            // single quotes must be broken up by the '\'' escape sequence.
            Assert.IsFalse(args.Any(a => a.Contains("'rm -rf /", StringComparison.Ordinal)), $"Escaping failed; argv: {string.Join(' ', args)}");
        }

        [TestMethod]
        public void BuildArgumentList_NormalizesCrLfInPayload()
        {
            var args = SshTransport.BuildArgumentList(KeyPath, VmIp, User, null, "a\r\nb");
            Assert.IsFalse(string.Concat(args).Contains('\r'), "CR must be stripped before the payload hits argv");
        }

        [TestMethod]
        public void EstimateCommandLineLength_CountsDoubledQuotes()
        {
            // Upper-bound estimator for the CreateProcess 32,767-char limit:
            // each element is quoted and every internal " doubles.
            int estimate = SshTransport.EstimateCommandLineLength(new[] { "a\"b", "plain" });
            Assert.AreEqual(/*"a"b"→"a""b" (7) + space*/ 7 + 1 + /*"plain" (7)*/ 7, estimate);
        }

        private static void AssertContainsPair(IReadOnlyList<string> args, string flag, string value)
        {
            for (int i = 0; i < args.Count - 1; i++)
                if (args[i] == flag && args[i + 1] == value)
                    return;
            Assert.Fail($"Expected consecutive elements [{flag}] [{value}] in: {string.Join(' ', args)}");
        }

        #endregion

        #region IdleAwareWaitAsync

        private static Task WaitCanceled(CancellationToken token)
            => Task.Delay(Timeout.Infinite, token);

        [TestMethod]
        public async Task IdleWait_ThrowsTimeout_WhenOutputGoesSilent()
        {
            var start = DateTime.UtcNow;
            // Fake: process never exits, last output is frozen at 'start'
            // (idle clock runs), wait delegate parks until canceled.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try
            {
                await SshTransport.IdleAwareWaitAsync(
                    hasExited: () => false,
                    waitForExitAsync: WaitCanceled,
                    lastStdoutAt: () => start,
                    idleTimeout: TimeSpan.FromMilliseconds(100),
                    totalTimeoutToken: cts.Token,
                    vmName: "idle-test-vm");
                Assert.Fail("Expected TimeoutException");
            }
            catch (TimeoutException tex)
            {
                StringAssert.Contains(tex.Message, "idle-test-vm");
                StringAssert.Contains(tex.Message, "no stdout");
            }
        }

        [TestMethod]
        public async Task IdleWait_Succeeds_WhenProcessExitsBeforeIdleBudget()
        {
            // Fake: process exits almost immediately — the idle budget is
            // never consulted past the first loop iteration.
            await SshTransport.IdleAwareWaitAsync(
                hasExited: () => true,
                waitForExitAsync: _ => Task.CompletedTask,
                lastStdoutAt: () => DateTime.UtcNow - TimeSpan.FromHours(1), // ancient: would trip idle
                idleTimeout: TimeSpan.FromSeconds(1),
                totalTimeoutToken: CancellationToken.None,
                vmName: "idle-test-vm");
        }

        [TestMethod]
        public async Task IdleWait_ResetsBudget_WhenOutputKeepsArriving()
        {
            // Fake: process exits after ~450 ms; last output tracks 'now' so
            // the idle clock never exceeds the 1 s budget despite the wait
            // outlasting a single budget window from loop start.
            var exitAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(450);
            await SshTransport.IdleAwareWaitAsync(
                hasExited: () => DateTime.UtcNow >= exitAt,
                waitForExitAsync: _ => Task.Delay(50),
                lastStdoutAt: () => DateTime.UtcNow,
                idleTimeout: TimeSpan.FromSeconds(1),
                totalTimeoutToken: CancellationToken.None,
                vmName: "idle-test-vm");
        }

        [TestMethod]
        public async Task IdleWait_TotalTimeoutStillBinds_WhileOutputFlows()
        {
            // Fake: output flows forever, process never exits; the TOTAL
            // token fires and surfaces as OperationCanceledException — the
            // idle budget must not mask the total deadline.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            try
            {
                await SshTransport.IdleAwareWaitAsync(
                    hasExited: () => false,
                    waitForExitAsync: WaitCanceled,
                    lastStdoutAt: () => DateTime.UtcNow,
                    idleTimeout: TimeSpan.FromSeconds(30),
                    totalTimeoutToken: cts.Token,
                    vmName: "idle-test-vm");
                Assert.Fail("Expected OperationCanceledException");
            }
            catch (OperationCanceledException) { /* expected */ }
        }

        #endregion

        [TestMethod]
        public void BuildArgumentList_AppliesCommonSshOptions()
        {
            var args = SshTransport.BuildArgumentList(KeyPath, VmIp, User, null, "echo hi");

            // BatchMode (never prompt), ConnectTimeout 10s, key file, target.
            AssertContainsPair(args, "-i", KeyPath);
            AssertContainsPair(args, "-o", "BatchMode=yes");
            AssertContainsPair(args, "-o", "ConnectTimeout=10");
            // ServerAlive keepalives: black-holed sessions (mid-pipeline IP
            // change) must die in ~15s, not sit for the whole command timeout.
            AssertContainsPair(args, "-o", "ServerAliveInterval=5");
            AssertContainsPair(args, "-o", "ServerAliveCountMax=3");
            StringAssert.Contains(string.Join(' ', args), $" {User}@{VmIp} ");
        }

        #region Escaping helpers
        [TestMethod]
        public void EscapeSingleQuotes_ReplacesQuoteWithShellSafe()
        {
            Assert.AreEqual("don'\\''t", SshTransport.EscapeSingleQuotes("don't"));
            Assert.AreEqual("plain", SshTransport.EscapeSingleQuotes("plain"));
            Assert.AreEqual("", SshTransport.EscapeSingleQuotes(""));
        }

        [TestMethod]
        public void EscapeForSsh_ProducesSingleQuotedShellWord()
        {
            // Round-trip: bash -c receives one literal argument whose single
            // quotes survive ssh argv parsing.
            Assert.AreEqual("'don'\\''t'", SshTransport.EscapeForSsh("don't"));
        }

        [TestMethod]
        public void EscapeForSsh_Empty_WrapsAsEmptyQuotedWord()
        {
            Assert.AreEqual("''", SshTransport.EscapeForSsh(""));
        }

        #endregion

        #region FilterSignificantStderr

        [TestMethod]
        public void FilterSignificantStderr_DropsNoiseLines()
        {
            string noisy = "Warning: Permanently added '192.168.42.7' (ED25519) to the list of known hosts.\r\n\r\nSome real error\n";
            string filtered = SshTransport.FilterSignificantStderr(noisy);

            StringAssert.Contains(filtered, "Some real error");
            Assert.IsFalse(filtered.Contains("Permanently added"), "The benign known-hosts notice must be filtered");
        }

        [TestMethod]
        public void FilterSignificantStderr_EmptyOrNull_ReturnsEmpty()
        {
            Assert.AreEqual("", SshTransport.FilterSignificantStderr(null));
            Assert.AreEqual("", SshTransport.FilterSignificantStderr(""));
            Assert.AreEqual("", SshTransport.FilterSignificantStderr("   \r\n  \n"));
        }
        #endregion

        #region ExecuteAsync maxArgumentLength guard

        [TestMethod]
        public async Task ExecuteAsync_OversizedCommand_FailsFastBeforeProcessLaunch()
        {
            // A command over the argv budget must throw the actionable
            // InvalidOperationException BEFORE ssh.exe is spawned (so this
            // test never touches the network or a live VM).
            string oversized = new string('A', 1000);

            InvalidOperationException? ex = null;
            try
            {
                await SshTransport.ExecuteAsync(
                    Mock.Of<ILogger>(), "test-vm", KeyPath, VmIp, User,
                    knownHostsFile: null, oversized, TimeSpan.FromSeconds(5),
                    CancellationToken.None, maxArgumentLength: 500);
                Assert.Fail("Expected the maxArgumentLength guard to throw before any process launch.");
            }
            catch (InvalidOperationException e)
            {
                ex = e;
            }

            StringAssert.Contains(ex.Message, "test-vm");
            StringAssert.Contains(ex.Message, "500");
            StringAssert.Contains(ex.Message, "CopyContentAsync");
        }

        #endregion

        #region ResetHostKeyPinning (via SshGuestShell)

        [TestMethod]
        public void ResetHostKeyPinning_CreatesFreshEmptyKnownHosts_AndSanitizesVmName()
        {
            string dir = Path.Combine(Path.GetTempPath(), "VMCreate", "known_hosts");
            Directory.CreateDirectory(dir);
            // Use a VM name with characters Windows forbids in file names.
            string rawName = "test:vm<with*weird?chars>";
            string expectedFile = Path.Combine(dir, "test_vm_with_weird_chars_.known_hosts");

            try
            {
                var shell = new SshGuestShell(Mock.Of<ILogger>(), rawName, KeyPath);

                shell.ResetHostKeyPinning();

                Assert.IsTrue(File.Exists(expectedFile),
                    $"Expected sanitized known_hosts at {expectedFile}");
                Assert.AreEqual(0L, new FileInfo(expectedFile).Length,
                    "Freshly armed known_hosts must be empty — ssh appends the entry on first accept-new connect.");
            }
            finally
            {
                if (File.Exists(expectedFile)) File.Delete(expectedFile);
            }
        }

        [TestMethod]
        public void ResetHostKeyPinning_ReplacesStaleEntryFromPreviousDeployment()
        {
            string rawName = "stalepin-test";
            string dir = Path.Combine(Path.GetTempPath(), "VMCreate", "known_hosts");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{rawName}.known_hosts");

            try
            {
                // Simulate a stale pin from an aborted deploy of the past.
                File.WriteAllText(path, "192.168.1.1 ssh-ed25519 AAAASTALE");

                var shell = new SshGuestShell(Mock.Of<ILogger>(), rawName, KeyPath);

                shell.ResetHostKeyPinning();

                Assert.IsTrue(File.Exists(path));
                Assert.AreEqual(0L, new FileInfo(path).Length,
                    "Stale known_hosts must be wiped so a re-created VM's regenerated host key can be re-learned.");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        #endregion

        #region PickNeighborMacMatch (KVP-blind ARP fallback)

        [TestMethod]
        public void PickNeighborMacMatch_MatchesAcrossMacRepresentations()
        {
            // Hyper-V reports "00155DB7346A"; Get-NetNeighbor reports
            // "00-15-5d-b7-34-6a". Both must match.
            var vmMacs = new[] { "00155DB7346A" };
            var neighbors = new[]
            {
                ("192.168.42.7", "00-15-5d-b7-34-6a", "Reachable"),
            };

            Assert.AreEqual("192.168.42.7", SshTransport.PickNeighborMacMatch(vmMacs, neighbors));
        }

        [TestMethod]
        public void PickNeighborMacMatch_PrefersReachableOverStaleRow()
        {
            var vmMacs = new[] { "00155DB7346A" };
            var neighbors = new[]
            {
                // Stale row listed first: a re-leased DHCP address leaves a
                // stale binding behind; the live Reachable one must win.
                ("192.168.42.99", "00-15-5d-b7-34-6a", "Stale"),
                ("192.168.42.7", "00:15:5d:b7:34:6a", "Reachable"),
            };

            Assert.AreEqual("192.168.42.7", SshTransport.PickNeighborMacMatch(vmMacs, neighbors));
        }

        [TestMethod]
        public void PickNeighborMacMatch_UsesAnyVmAdapterMac_TempFirstUnneeded()
        {
            // The caller passes all adapter MACs; any of them matching wins.
            var vmMacs = new[] { "00155D000001", "00155DB7346A" };
            var neighbors = new[]
            {
                ("192.168.42.7", "00-15-5D-B7-34-6A", "Permanent"),
            };

            Assert.AreEqual("192.168.42.7", SshTransport.PickNeighborMacMatch(vmMacs, neighbors));
        }

        [TestMethod]
        public void PickNeighborMacMatch_IgnoresIncompleteAndFailedRows()
        {
            // An Incomplete row means ARP never resolved — no SSH will ever
            // answer there.
            var vmMacs = new[] { "00155DB7346A" };
            var neighbors = new[]
            {
                ("10.0.0.55", "00-15-5d-b7-34-6a", "Incomplete"),
                ("10.0.0.56", "00-15-5d-b7-34-6a", "Unreachable"),
                ("192.168.42.7", "00-15-5d-b7-34-6a", "Delay"),
            };

            Assert.AreEqual("192.168.42.7", SshTransport.PickNeighborMacMatch(vmMacs, neighbors));
        }

        [TestMethod]
        public void PickNeighborMacMatch_SkipsApipaAndNonMatchingMacs()
        {
            var vmMacs = new[] { "00155DB7346A" };
            var neighbors = new[]
            {
                // Link-local can never carry SSH for us.
                ("169.254.10.20", "00-15-5d-b7-34-6a", "Reachable"),
                // A different host's VM — never match.
                ("192.168.42.8", "00-15-5d-aa-bb-cc", "Reachable"),
            };

            Assert.IsNull(SshTransport.PickNeighborMacMatch(vmMacs, neighbors));
        }

        [TestMethod]
        public void PickNeighborMacMatch_CimInstanceWithEmptyProperties_NoMatch()
        {
            // PRODUCTION BUG REPRO: Under CreateDefault2 runspace,
            // Get-NetNeighbor returns CimInstance objects whose Properties
            // dictionary is EMPTY. The extraction code then produces tuples
            // with all-empty strings. With an empty state string, no rank
            // matches, so the function must return null (and the caller keeps
            // polling until the runspace returns proper PSObjects).
            //
            // This test guards against a regression where empty states
            // accidentally match.
            var vmMacs = new[] { "00155D100185" };
            var neighbors = new[]
            {
                // Simulating the CimInstance extraction result: all empty
                ("", "", ""),
                // The correct row with a valid state
                ("172.23.185.39", "00-15-5D-10-01-85", "Permanent"),
            };

            Assert.AreEqual("172.23.185.39", SshTransport.PickNeighborMacMatch(vmMacs, neighbors),
                "rows with empty properties must be skipped, not matched");
        }

        [TestMethod]
        public void PickNeighborMacMatch_AllRowsEmptyProperties_ReturnsNull()
        {
            // If EVERY row has empty properties (the full CimInstance case),
            // discovery must fail gracefully and return null so the caller
            // retries — never match a garbage address.
            var vmMacs = new[] { "00155D100185" };
            var neighbors = new[]
            {
                ("", "", ""),
                ("", "", ""),
            };

            Assert.IsNull(SshTransport.PickNeighborMacMatch(vmMacs, neighbors),
                "all-empty rows must not match anything");
        }

        [TestMethod]
        public void PickNeighborMacMatch_SkipsApipaAndNonMatchingMacs_MustIgnoreEmptyIpRows()
        {
            var vmMacs = new[] { "00155DB7346A" };
            var neighbors = new[]
            {
                // Link-local can never carry SSH for us.
                ("169.254.10.20", "00-15-5d-b7-34-6a", "Reachable"),
                // A different host's VM — never match.
                ("192.168.42.8", "00-15-5d-aa-bb-cc", "Reachable"),
                // Empty-IP row from CimInstance property extraction failure
                ("", "", ""),
            };

            Assert.IsNull(SshTransport.PickNeighborMacMatch(vmMacs, neighbors));
        }

        [TestMethod]
        public void PickNeighborMacMatch_EmptyInputs_ReturnsNull()
        {
            Assert.IsNull(SshTransport.PickNeighborMacMatch(
                Array.Empty<string>(),
                new[] { ("192.168.42.7", "00-15-5d-b7-34-6a", "Reachable") }));
            Assert.IsNull(SshTransport.PickNeighborMacMatch(
                new[] { "00155DB7346A" }, Array.Empty<(string, string, string)>()));
        }

        [TestMethod]
        public void PickNeighborMacMatch_IgnoresAllZeroPlaceholderMacRow()
        {
            // The observed production bug: the host keeps an Unreachable row for a
            // dead address whose LinkLayerAddress is the shared all-zero
            // placeholder (00-00-00-00-00-00). If any VM adapter reports a zero MAC
            // (NIC not yet initialized), that row matches and discovery pins a dead
            // IP — Omarchy reported 172.23.186.21 while the guest was 172.23.180.201.
            var vmMacs = new[] { "00155D100182", "000000000000" };
            var neighbors = new[]
            {
                ("172.23.186.21", "00-00-00-00-00-00", "Unreachable"),
                ("172.23.180.201", "00-15-5D-10-01-82", "Permanent"),
            };

            Assert.AreEqual("172.23.180.201", SshTransport.PickNeighborMacMatch(vmMacs, neighbors),
                "the zero-MAC placeholder row must never win over the real adapter binding");
        }

        [TestMethod]
        public void PickNeighborMacMatch_ZeroMacVmAdapter_Alone_ReturnsNull()
        {
            // A VM whose only adapter reports the zero placeholder MAC has no
            // usable identity in the neighbor table — better to keep polling than
            // to latch onto a shared placeholder row.
            var vmMacs = new[] { "000000000000" };
            var neighbors = new[]
            {
                ("172.23.186.21", "00-00-00-00-00-00", "Unreachable"),
            };

            Assert.IsNull(SshTransport.PickNeighborMacMatch(vmMacs, neighbors));
        }

        [TestMethod]
        public void PickNeighborMacMatch_IgnoresBroadcastMacRow()
        {
            var vmMacs = new[] { "FFFFFFFFFFFF", "00155DB7346A" };
            var neighbors = new[]
            {
                ("172.23.191.255", "FF-FF-FF-FF-FF-FF", "Permanent"),
                ("192.168.42.7", "00-15-5d-b7-34-6a", "Reachable"),
            };

            Assert.AreEqual("192.168.42.7", SshTransport.PickNeighborMacMatch(vmMacs, neighbors),
                "the broadcast MAC row must never match");
        }

        [TestMethod]
        public void NormalizeMacAddress_StripsSeparatorsAndUnifiesCase()
        {
            Assert.AreEqual("00155DB7346A", SshTransport.NormalizeMacAddress("00-15-5d-b7-34-6a"));
            Assert.AreEqual("00155DB7346A", SshTransport.NormalizeMacAddress("00:15:5D:b7:34:6a"));
            Assert.AreEqual("00155DB7346A", SshTransport.NormalizeMacAddress("00155DB7346A"));
            Assert.AreEqual("", SshTransport.NormalizeMacAddress(null));
            Assert.AreEqual("", SshTransport.NormalizeMacAddress(""));
        }

        [TestMethod]
        public void IsSshTransportError_ClassifiesTimeoutAndKeepaliveDeaths()
        {
            // Host-side command timeout: may be a black-holed session after a
            // mid-pipeline IP change — must reach the retry+re-discovery path.
            Assert.IsTrue(SshGuestShell.IsSshTransportError(
                new TimeoutException("SSH command timed out after 120s on VM 'x'")));

            // ssh exit 255 signatures of keepalive exhaustion / dead peer.
            Assert.IsTrue(SshGuestShell.IsSshTransportError(
                new Exception("SSH command failed (exit code 255) on VM 'x': Timeout, server not responding.")));
            Assert.IsTrue(SshGuestShell.IsSshTransportError(
                new Exception("ssh: connect to host 172.23.180.227 port 22: Connection refused")));
            Assert.IsTrue(SshGuestShell.IsSshTransportError(
                new Exception("ssh: connect to host 172.23.180.227 port 22: Connection timed out")));

            // Guest-side failures are NOT transport errors — no retry.
            Assert.IsFalse(SshGuestShell.IsSshTransportError(
                new Exception("SSH command failed (exit code 1) on VM 'x': bash: oops: command not found")));
            Assert.IsFalse(SshGuestShell.IsSshTransportError(
                new InvalidOperationException("VM IP address not discovered yet.")));
        }

        [TestMethod]
        public void IsUsableMacAddress_RejectsPlaceholderAndBroadcast()
        {
            Assert.IsFalse(SshTransport.IsUsableMacAddress("00-00-00-00-00-00"));
            Assert.IsFalse(SshTransport.IsUsableMacAddress("000000000000"));
            Assert.IsFalse(SshTransport.IsUsableMacAddress("FF-FF-FF-FF-FF-FF"));
            Assert.IsFalse(SshTransport.IsUsableMacAddress("ffffffffffff"));
            Assert.IsFalse(SshTransport.IsUsableMacAddress(null));
            Assert.IsFalse(SshTransport.IsUsableMacAddress(""));
            Assert.IsFalse(SshTransport.IsUsableMacAddress("00-15-5d"), "too short to be a real EUI-48");

            Assert.IsTrue(SshTransport.IsUsableMacAddress("00-15-5d-b7-34-6a"));
            Assert.IsTrue(SshTransport.IsUsableMacAddress("00155DB7346A"));
        }

        #endregion

        #region NormalizeNeighborState (CreateDefault2 raw-byte State)

        [TestMethod]
        public void ReadNeighborRow_ByteState_ProducesEnumName()
        {
            // END-TO-END SEAM: build a PSObject whose State property is the raw
            // System.Byte the CreateDefault2 runspace returns for MSFT_NetNeighbor,
            // and confirm ReadNeighborRow surfaces the enum name PickNeighborMacMatch
            // ranks on. This is the exact shape the probe captured in the app's
            // hosted runspace (State=[6], stateType=System.Byte).
            var psObj = new System.Management.Automation.PSObject();
            psObj.Properties.Add(new System.Management.Automation.PSNoteProperty("IPAddress", "172.23.185.39"));
            psObj.Properties.Add(new System.Management.Automation.PSNoteProperty("LinkLayerAddress", "00-15-5D-10-01-85"));
            psObj.Properties.Add(new System.Management.Automation.PSNoteProperty("State", (byte)6));

            var row = SshTransport.ReadNeighborRow(psObj);

            Assert.AreEqual("172.23.185.39", row.Ip);
            Assert.AreEqual("00-15-5D-10-01-85", row.Mac);
            Assert.AreEqual("Permanent", row.State,
                "the raw CIM byte must normalize to the enum name the rank filter matches");
        }

        [TestMethod]
        public void ReadNeighborRow_MissingStateProperty_ProducesEmptyState()
        {
            // Defensive: a row without a State property must not throw and must
            // yield an unusable (empty) state.
            var psObj = new System.Management.Automation.PSObject();
            psObj.Properties.Add(new System.Management.Automation.PSNoteProperty("IPAddress", "172.23.185.39"));

            var row = SshTransport.ReadNeighborRow(psObj);
            Assert.AreEqual("", row.State);
        }

        [TestMethod]
        [DataRow((byte)0, "Unreachable")]
        [DataRow((byte)1, "Incomplete")]
        [DataRow((byte)2, "Probe")]
        [DataRow((byte)3, "Delay")]
        [DataRow((byte)4, "Stale")]
        [DataRow((byte)5, "Reachable")]
        [DataRow((byte)6, "Permanent")]
        [DataRow((byte)7, "Failed")]
        public void NormalizeNeighborState_ByteValues_MapToEnumNames(byte raw, string expected)
        {
            // The hosted CreateDefault2 runspace surfaces MSFT_NetNeighbor.State
            // as a raw System.Byte (probe-verified: Default Switch rows read 6,
            // stale rows read 4). PickNeighborMacMatch ranks by enum NAME, so
            // without this mapping the KVP-blind fallback never matches.
            Assert.AreEqual(expected, SshTransport.NormalizeNeighborState(raw));
        }

        [TestMethod]
        public void NormalizeNeighborState_ByteString_ParsesViaNumeric()
        {
            // If anything upstream already stringified the byte ("6"), recover it.
            Assert.AreEqual("Permanent", SshTransport.NormalizeNeighborState("6"));
            Assert.AreEqual("Stale", SshTransport.NormalizeNeighborState("4"));
        }

        [TestMethod]
        public void NormalizeNeighborState_EnumNameString_PassesThrough()
        {
            // A fully-adapted session hands us the enum name string.
            Assert.AreEqual("Reachable", SshTransport.NormalizeNeighborState("Reachable"));
        }

        [TestMethod]
        public void NormalizeNeighborState_EnumValue_MapsViaUnderlyingByte()
        {
            // DayOfWeek.Saturday has underlying value 6 → must map to Permanent,
            // proving the enum path routes through the numeric mapping.
            Assert.AreEqual("Permanent", SshTransport.NormalizeNeighborState(DayOfWeek.Saturday));
            Assert.AreEqual("Unreachable", SshTransport.NormalizeNeighborState(DayOfWeek.Sunday));
        }

        [TestMethod]
        public void NormalizeNeighborState_NullAndUnknown_ReturnEmpty()
        {
            Assert.AreEqual("", SshTransport.NormalizeNeighborState(null));
            Assert.AreEqual("", SshTransport.NormalizeNeighborState((byte)99));
            Assert.AreEqual("", SshTransport.NormalizeNeighborState(new object()), "unrecognized shape must never rank as usable");
        }

        #endregion

        #region PickGuestIp (stale-KVP vs neighbor tie-break)

        [TestMethod]
        public void PickGuestIp_NeighborRowWins_OverDisagreeingStaleKvp()
        {
            // PRODUCTION SCENARIO: installer live ISO reported KVP lease
            // 172.23.191.16; guest rebooted into the KVP-blind installed system
            // and got 172.23.182.209. The neighbor table (ARP) tracks the live
            // binding; KVP is frozen at the old lease. The neighbor row must win.
            Assert.AreEqual("172.23.182.209",
                SshTransport.PickGuestIp("172.23.191.16", "172.23.182.209"));
        }

        [TestMethod]
        public void PickGuestIp_KvpWins_WhenNoNeighborMatch()
        {
            // KVP is live and ARP has nothing usable yet → trust KVP.
            Assert.AreEqual("172.23.191.16", SshTransport.PickGuestIp("172.23.191.16", null));
            Assert.AreEqual("172.23.191.16", SshTransport.PickGuestIp("172.23.191.16", ""));
        }

        [TestMethod]
        public void PickGuestIp_NeighborWins_WhenKvpBlind()
        {
            // The pure KVP-blind installed system: no KVP IP at all.
            Assert.AreEqual("172.23.182.209", SshTransport.PickGuestIp(null, "172.23.182.209"));
        }

        [TestMethod]
        public void PickGuestIp_NeitherKnows_ReturnsNull()
        {
            Assert.IsNull(SshTransport.PickGuestIp(null, null));
            Assert.IsNull(SshTransport.PickGuestIp("", ""));
        }

        #endregion
    }
}
