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
        public async Task UploadsAllFourScripts_ThenRunsScenarioWithArgs()
        {
            var result = await RunAsync("clone", "ssh-ed25519 AAAAKEY vmcreate user@host");
            Assert.IsTrue(result.Success, result.ErrorMessage);

            string? streamed = _commands.FirstOrDefault(c => c.StartsWith("[stream] "));
            Assert.IsNotNull(streamed, "scenario must run streamed");
            StringAssert.Contains(streamed, "sudo bash /opt/vmcreate/clone_disk.sh");
            // xrdp flag leads the argv (false by default — asserted via the
            // word boundary so 'false' doesn't match inside other words).
            Assert.IsTrue(streamed.Contains("/clone_disk.sh false "), "xrdp flag position");
            // Pubkey with spaces arrives as ONE single-quoted argv word.
            StringAssert.Contains(streamed, "'ssh-ed25519 AAAAKEY vmcreate user@host'");
        }

        [TestMethod]
        public async Task PubkeyWithSingleQuote_IsShellEscaped()
        {
            var result = await RunAsync("customize", "ssh-ed25519 AAAA it's a key");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            string? streamed = _commands.FirstOrDefault(c => c.StartsWith("[stream] "));
            Assert.IsNotNull(streamed);
            StringAssert.Contains(streamed, "'ssh-ed25519 AAAA it'\\''s a key'");
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
