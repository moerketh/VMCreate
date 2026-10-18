using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.IO;
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

        #region BuildArguments

        [TestMethod]
        public void BuildArguments_TofuMode_UsesAcceptNewAndPerVmKnownHosts()
        {
            string knownHosts = @"C:\Users\t\AppData\Local\Temp\VMCreate\known_hosts\test-vm.known_hosts";
            string args = SshTransport.BuildArguments(KeyPath, VmIp, User, knownHosts, "echo hi");

            StringAssert.Contains(args, "-o StrictHostKeyChecking=accept-new");
            StringAssert.Contains(args, $"-o UserKnownHostsFile=\"{knownHosts}\"");
            Assert.IsFalse(args.Contains("StrictHostKeyChecking=no"), "TOFU mode must not disable host-key checking");
            Assert.IsFalse(args.Contains("UserKnownHostsFile=NUL"), "TOFU mode must not discard host keys");
        }

        [TestMethod]
        public void BuildArguments_NoPinningMode_UsesNoAndNul()
        {
            string args = SshTransport.BuildArguments(KeyPath, VmIp, User, null, "echo hi");

            StringAssert.Contains(args, "-o StrictHostKeyChecking=no");
            StringAssert.Contains(args, "-o UserKnownHostsFile=NUL");
            Assert.IsFalse(args.Contains("accept-new"), "Diagnostics mode must stay permissive (transient ISO guest)");
        }

        [TestMethod]
        public void BuildArguments_VsockTarget_UsesGuidHostAndAbsoluteHvcProxyCommand()
        {
            var target = SshConnectionTarget.ForVsock("d811e738-5c46-4f57-a171-4f822a38f22a");
            string args = SshTransport.BuildArguments(KeyPath, target, User, null, "echo hi");

            // Destination is the GUID, not an IP.
            StringAssert.Contains(args, $"{User}@d811e738-5c46-4f57-a171-4f822a38f22a");
            // ProxyCommand carries hvc by ABSOLUTE expanded path (anti-hijack,
            // same posture as SshExePath) and the vsock transport flag.
            StringAssert.Contains(args, "ProxyCommand=");
            StringAssert.Contains(args, "nc -t vsock %h 22");
            Assert.IsTrue(
                args.Contains(@"\System32\hvc.exe") || args.Contains("/System32/hvc.exe"),
                "ProxyCommand must invoke hvc.exe by absolute path");
            Assert.IsFalse(args.Contains("%SystemRoot%"), "ssh expands %-tokens in ProxyCommand itself; the path must arrive pre-expanded");
        }

        [TestMethod]
        public void BuildArguments_VsockTarget_NoPercentTokensExceptProxyHost()
        {
            // ssh performs %-token substitution inside ProxyCommand values:
            // any other % in the argument line would be misread (e.g.
            // %SystemRoot% as %S + "ystemRoot%"). Assert the ONLY % in the
            // vsock argument line is the intended %h host token.
            var target = SshConnectionTarget.ForVsock("d811e738-5c46-4f57-a171-4f822a38f22a");
            string args = SshTransport.BuildArguments(KeyPath, target, User, null, "echo hi");

            int percentCount = 0;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == '%') percentCount++;
            Assert.AreEqual(1, percentCount, $"exactly one % (the %h token) expected, got {percentCount}: {args}");
            StringAssert.Contains(args, "%h");
        }

        [TestMethod]
        public void BuildArguments_IpTarget_HasNoProxyCommand()
        {
            // The IP path must remain byte-identical in behavior: no vsock
            // arguments leak into post-boot connections.
            string args = SshTransport.BuildArguments(KeyPath, VmIp, User, null, "echo hi");
            Assert.IsFalse(args.Contains("ProxyCommand"), "IP target must not carry a ProxyCommand");
        }

        [TestMethod]
        public void BuildArguments_AppliesCommonSshOptions()
        {
            string args = SshTransport.BuildArguments(KeyPath, VmIp, User, null, "echo hi");

            // BatchMode (never prompt), ConnectTimeout 10s, key file, target.
            StringAssert.Contains(args, "-o BatchMode=yes");
            StringAssert.Contains(args, "-o ConnectTimeout=10");
            // ServerAlive keepalives: black-holed sessions (mid-pipeline IP
            // change) must die in ~15s, not sit for the whole command timeout.
            StringAssert.Contains(args, "-o ServerAliveInterval=5");
            StringAssert.Contains(args, "-o ServerAliveCountMax=3");
            StringAssert.Contains(args, $"-i \"{KeyPath}\"");
            StringAssert.Contains(args, $" {User}@{VmIp} ");
        }

        [TestMethod]
        public void BuildArguments_WrapsCommandInBashC()
        {
            string args = SshTransport.BuildArguments(KeyPath, VmIp, User, null, "echo 'ready'");
            // Payload "echo 'ready'" → 'echo '\''ready'\''' (open quote, escaped
            // inner quote pair, close quote): the whole command is ONE literal
            // shell word following bash -c.
            string expectedTail = "bash -c " + "'echo '\\''ready'\\'''" ;
            Assert.IsTrue(args.EndsWith(expectedTail, StringComparison.Ordinal),
                $"Expected trailing bash -c with single-quote-escaped payload, got: {args}");
        }

        [TestMethod]
        public void BuildArguments_PayloadIsFullyEscaped()
        {
            // A quote-terminated command followed by a second command must be
            // impossible: the shell-safe single-quote escape makes the whole
            // payload one literal bash word.
            string malicious = "echo 'don''t'; rm -rf /";
            string args = SshTransport.BuildArguments(KeyPath, VmIp, User, null, malicious);

            // The raw payload must NOT appear as-is anywhere in the args: the
            // single quotes must be broken up by the '\'' escape sequence.
            Assert.IsFalse(args.Contains("'rm -rf /"), $"Escaping failed; args: {args}");
        }

        [TestMethod]
        public void BuildArguments_NormalizesCrLfInPayload()
        {
            string args = SshTransport.BuildArguments(KeyPath, VmIp, User, null, "a\r\nb");
            Assert.IsFalse(args.Contains("\r"), "CR must be stripped before the payload hits argv");
        }

        #endregion

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