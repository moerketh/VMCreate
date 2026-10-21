using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV.VmCreation;

namespace VMCreate.Tests.HyperV.VmCreation
{
    /// <summary>
    /// Tests for the W2 embedded-script runner and protocol parser. The
    /// shell is faked; the assertions pin the upload sequence, argv
    /// quoting, error paths and protocol routing — the exact surfaces that
    /// broke in the first review round (unsudoable chmod, /opt/autorun
    /// leftovers, DNS regression).
    /// </summary>
    [TestClass]
    public class EmbeddedIsoScriptRunnerTests
    {
        private Mock<IHostNetworkService> _hostNetwork = null!;
        private Mock<SshGuestShell> _shell = null!;
        private EmbeddedIsoScriptRunner _runner = null!;

        private readonly List<string> _commands = new();

        [TestInitialize]
        public void Setup()
        {
            _hostNetwork = new Mock<IHostNetworkService>();
            _shell = new Mock<SshGuestShell>(
                Mock.Of<ILogger>(), "vm", @"C:\k\id", "vmcreate",
                SshConnectionTarget.ForVsock("d811e738-5c46-4f57-a171-4f822a38f22a"));
            _runner = new EmbeddedIsoScriptRunner(
                _hostNetwork.Object, Mock.Of<ILogger<EmbeddedIsoScriptRunner>>());

            _shell
                .Setup(m => m.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("")
                .Callback((string cmd, CancellationToken _) => _commands.Add(cmd));
            _shell
                .Setup(m => m.RunStreamingCommandAsync(
                    It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(),
                    It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("")
                .Callback((string cmd, TimeSpan _, TimeSpan _, Action<string> onLine, CancellationToken _) =>
                {
                    _commands.Add($"[stream] {cmd}");
                    // Simulate a minimal guest: our own command echo plus
                    // protocol lines.
                    onLine("VMCREATE:PROGRESS:50:halfway");
                    onLine("VMCREATE:MSG:working");
                    onLine("VMCREATE:HVSOCK:yes");
                    onLine("random partclone noise 43.12%");
                });
        }

        private Task<IsoBootCycleResult> RunAsync(string scenario = "customize", string? pubkey = null) =>
            _runner.RunAsync(_shell.Object, scenario, pubkey, null!, new VmCustomizations(), null, CancellationToken.None);

        [TestMethod]
        public async Task UploadSequence_MkdirOnce_FourCopies_NoChmod()
        {
            var result = await RunAsync();
            Assert.IsTrue(result.Success, result.ErrorMessage);

            Assert.AreEqual(1, _commands.Count(c => c == "sudo mkdir -p /opt/vmcreate"), "exactly one mkdir");
            // Four scripts copied (CopyContentAsync is a virtual on the
            // mocked shell; its invocation is recorded via the mock below).
            _shell.Verify(m => m.CopyContentAsync(It.IsAny<string>(), It.Is<string>(p => p == "/opt/vmcreate/common.sh"), It.IsAny<CancellationToken>()), Times.Once);
            _shell.Verify(m => m.CopyContentAsync(It.IsAny<string>(), It.Is<string>(p => p == "/opt/vmcreate/customize_target.sh"), It.IsAny<CancellationToken>()), Times.Once);
            _shell.Verify(m => m.CopyContentAsync(It.IsAny<string>(), It.Is<string>(p => p == "/opt/vmcreate/install_grub.sh"), It.IsAny<CancellationToken>()), Times.Once);
            _shell.Verify(m => m.CopyContentAsync(It.IsAny<string>(), It.Is<string>(p => p == "/opt/vmcreate/install_xrdp.sh"), It.IsAny<CancellationToken>()), Times.Once);
            // The chmod regression: unprivileged chmod after a root-owned
            // 644 copy ALWAYS failed ("Operation not permitted"). It must
            // never come back.
            Assert.AreEqual(0, _commands.Count(c => c.Contains("chmod")), "chmod must not appear");
        }

        [TestMethod]
        public async Task UploadedScripts_ReferenceOnlyV2Paths()
        {
            // Blocker regression guard: the v1 /opt/autorun path survived
            // the first port and killed every clone at the GRUB step (cp
            // failed under set -e) and silently skipped xrdp on customize.
            foreach (string script in new[] { "clone_disk.sh", "customize_target.sh", "common.sh", "install_grub.sh", "install_xrdp.sh" })
            {
                string content = ScriptResourceLoader.Load(script);
                Assert.IsFalse(content.Contains("/opt/autorun"),
                    $"{script} still references the v1 /opt/autorun path");
            }
        }

        [TestMethod]
        public async Task UploadsAllFourScripts_ThenRunsPrepareAndFinalizePhases()
        {
            var result = await RunAsync("clone", "ssh-ed25519 AAAAKEY vmcreate user@host");
            Assert.IsTrue(result.Success, result.ErrorMessage);

            var streamed = _commands.Where(c => c.StartsWith("[stream] ")).ToList();
            Assert.AreEqual(2, streamed.Count, "prepare + finalize phases");
            StringAssert.Contains(streamed[0], "sudo bash /opt/vmcreate/clone_disk.sh");
            // prepare argv: xrdp flag leads (false by default), nameservers,
            // swap. No pubkey — that belongs to finalize.
            StringAssert.Contains(streamed[0], "/clone_disk.sh prepare false ");
            Assert.IsFalse(streamed[0].Contains("AAAAKEY"), "pubkey must not leak into prepare argv");
            // finalize argv: the pubkey as ONE single-quoted argv word.
            StringAssert.Contains(streamed[1], "/clone_disk.sh finalize 'ssh-ed25519 AAAAKEY vmcreate user@host'");
        }

        [TestMethod]
        public async Task ChrootSteps_RunBetweenPhases_ThroughChrootShell()
        {
            var seen = new List<GuestExecutionContext>();

            var result = await _runner.RunAsync(
                _shell.Object, "customize", null, null!, new VmCustomizations(), null, CancellationToken.None,
                (chrootShell, ct) =>
                {
                    seen.Add(chrootShell.Context);
                    _commands.Add("[chroot-steps]");
                    return Task.CompletedTask;
                });

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(1, seen.Count, "delegate runs exactly once");
            Assert.AreEqual(GuestExecutionContext.Chroot, seen[0], "delegate receives the chroot shell");

            // Ordering: the delegate must run BETWEEN the two streamed phases.
            int prepare = _commands.FindIndex(c => c.StartsWith("[stream] ") && c.Contains(" prepare "));
            int steps = _commands.IndexOf("[chroot-steps]");
            int finalize = _commands.FindLastIndex(c => c.StartsWith("[stream] ") && c.Contains(" finalize "));
            Assert.IsTrue(prepare >= 0 && steps > prepare && finalize > steps,
                $"expected prepare < chroot steps < finalize, got {prepare}/{steps}/{finalize}");
        }

        [TestMethod]
        public async Task PrepareFailure_TriggersCleanup_AndReturnsFailure()
        {
            var cleanupCalls = new List<string>();
            _shell
                .Setup(m => m.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("")
                .Callback((string cmd, TimeSpan _, CancellationToken _) => cleanupCalls.Add(cmd));
            _shell
                .Setup(m => m.RunStreamingCommandAsync(
                    It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(),
                    It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
                .Throws(new InvalidOperationException("guest exploded"));

            var result = await RunAsync();

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, "guest exploded");
            CollectionAssert.AreEqual(new[] { "sudo bash /opt/vmcreate/customize_target.sh cleanup" }, cleanupCalls,
                "failure path must sync+unmount the target before the runner powers off");
        }

        [TestMethod]
        public async Task ChrootStepFailure_TriggersCleanup_AndReturnsFailure()
        {
            var cleanupCalls = new List<string>();
            _shell
                .Setup(m => m.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("")
                .Callback((string cmd, TimeSpan _, CancellationToken _) => cleanupCalls.Add(cmd));

            var result = await _runner.RunAsync(
                _shell.Object, "customize", null, null!, new VmCustomizations(), null, CancellationToken.None,
                (_, _) => throw new InvalidOperationException("step blew up"));

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, "chroot steps failed");
            StringAssert.Contains(result.ErrorMessage, "step blew up");
            Assert.AreEqual(1, cleanupCalls.Count, "cleanup called once");
        }

        [TestMethod]
        public async Task CleanupFailure_IsNonFatal_OriginalErrorWins()
        {
            _shell
                .Setup(m => m.RunStreamingCommandAsync(
                    It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(),
                    It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
                .Throws(new InvalidOperationException("guest exploded"));
            _shell
                .Setup(m => m.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("cleanup timed out"));

            var result = await RunAsync();

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, "guest exploded",
                "the original phase error must not be masked by the cleanup failure");
        }

        [TestMethod]
        public async Task PubkeyWithSingleQuote_IsShellEscaped_InFinalize()
        {
            var result = await RunAsync("customize", "ssh-ed25519 AAAA it's a key");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            var finalize = _commands.LastOrDefault(c => c.StartsWith("[stream] "));
            Assert.IsNotNull(finalize, "finalize phase must run");
            StringAssert.Contains(finalize, "'ssh-ed25519 AAAA it'\\''s a key'");
        }

        [TestMethod]
        public async Task UnknownScenario_Throws()
        {
            try
            {
                await RunAsync("bogus");
                Assert.Fail("expected ArgumentException");
            }
            catch (ArgumentException) { /* expected */ }
        }

        [TestMethod]
        public async Task HostDnsMode_UsesHostResolver()
        {
            _hostNetwork.Setup(m => m.ResolveHostDnsServers()).Returns("10.1.2.3");
            var result = await RunAsync();
            Assert.IsTrue(result.Success, result.ErrorMessage);
            string? streamed = _commands.FirstOrDefault(c => c.StartsWith("[stream] "));
            Assert.IsNotNull(streamed);
            StringAssert.Contains(streamed, "'10.1.2.3'", "Host DNS mode must pass the host's resolvers");
        }

        [TestMethod]
        public async Task CancelPropagates_AsCancellation_NotFailure()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel(); // the when-filter requires an actually-cancelled token
            _shell
                .Setup(m => m.RunStreamingCommandAsync(
                    It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(),
                    It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
                .Throws(new OperationCanceledException(cts.Token));

            try
            {
                await _runner.RunAsync(_shell.Object, "customize", null, null!, new VmCustomizations(), null, cts.Token);
                Assert.Fail("expected cancellation to propagate");
            }
            catch (OperationCanceledException) { /* expected: not Failed() */ }
        }

        [TestMethod]
        public async Task NonStreamingShell_ThrowsActionable()
        {
            var plain = new Mock<IGuestShell>();
            plain.Setup(m => m.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");
            try
            {
                await _runner.RunAsync(plain.Object, "customize", null, null!, new VmCustomizations(), null, CancellationToken.None);
                Assert.Fail("expected InvalidOperationException");
            }
            catch (InvalidOperationException ex)
            {
                StringAssert.Contains(ex.Message, "SshGuestShell");
            }
        }

        [TestMethod]
        public void Parser_RoutesProtocolLines_AndIgnoresNoise()
        {
            CreateVMProgressInfo? sawProgress = null, sawMsg = null;
            var progress = new Progress<CreateVMProgressInfo>(p =>
            {
                if (p.ProgressPercentage > 0) sawProgress = p; else sawMsg = p;
            });
            var parser = new ProtocolLineParser(progress, Mock.Of<ILogger>(), "test");

            parser.OnLine("VMCREATE:PROGRESS:42:cloning root");
            parser.OnLine("VMCREATE:MSG:installing things");
            parser.OnLine("VMCREATE:HVSOCK:yes");
            parser.OnLine("partclone: 43.12% done");

            Assert.AreEqual("yes", parser.HvsockCapability);
            Assert.IsTrue(parser.CollectedOutput.Contains("43.12%"), "noise is collected for diagnostics");
        }
    }
}
