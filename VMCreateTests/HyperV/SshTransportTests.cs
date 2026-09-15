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
        public void BuildArguments_AppliesCommonSshOptions()
        {
            string args = SshTransport.BuildArguments(KeyPath, VmIp, User, null, "echo hi");

            // BatchMode (never prompt), ConnectTimeout 10s, key file, target.
            StringAssert.Contains(args, "-o BatchMode=yes");
            StringAssert.Contains(args, "-o ConnectTimeout=10");
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
    }
}