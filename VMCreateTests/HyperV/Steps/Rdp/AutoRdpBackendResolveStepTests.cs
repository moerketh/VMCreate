using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Tests for <see cref="AutoRdpBackendResolveStep"/> — the runtime
    /// Auto-backend resolver. Core contract: it must NEVER fail the
    /// deployment (detection is a routing decision, not a deployment
    /// step) — every failure routes conservatively to xrdp with a loud
    /// warning, and it mutates <see cref="VmCustomizations.RdpBackend"/>
    /// in place so later steps and the deploy UI see the resolved choice.
    /// <para>
    /// The shell mock serves BOTH <see cref="IGuestShell.RunCommandAsync"/>
    /// overloads: the 2-arg overload (no timeout) is used by
    /// <see cref="DistroDetector"/> for the /etc/os-release read; the
    /// TimeSpan overload carries the detection-script output.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class AutoRdpBackendResolveStepTests
    {
        private AutoRdpBackendResolveStep _step;
        private Mock<IGuestShell> _shell;
        private Mock<ILogger<AutoRdpBackendResolveStep>> _logger;
        private GalleryItem _lamcoCapableItem;

        [TestInitialize]
        public void Setup()
        {
            _step = new AutoRdpBackendResolveStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            // Default guest OS: Debian-family (Lamco-capable) so the
            // display-server verdict alone decides.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("ID=debian\n");
            _logger = new Mock<ILogger<AutoRdpBackendResolveStep>>();
            _lamcoCapableItem = new GalleryItem { LinuxDistro = LinuxDistro.Kali };
        }

        private void SetupScriptOutput(string output)
            => _shell.Setup(s => s.RunCommandAsync(
                    It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(output);

        private static bool LoggedWarning<T>(Mock<ILogger<T>> logger)
            => logger.Invocations.Any(i =>
                i.Method.Name == "Log" &&
                (LogLevel)i.Arguments[0] == LogLevel.Warning);

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Auto-select RDP backend", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(232, _step.Order);
            Assert.AreEqual("Sub_AutoRdpResolve", _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_OnlyWhenBackendStillAuto()
        {
            var auto = new VmCustomizations { RdpBackend = RdpBackend.Auto };
            Assert.IsTrue(_step.IsApplicable(_lamcoCapableItem, auto));

            foreach (var backend in new[] { RdpBackend.Xrdp, RdpBackend.Lamco, RdpBackend.None })
                Assert.IsFalse(_step.IsApplicable(_lamcoCapableItem, new VmCustomizations { RdpBackend = backend }),
                    $"IsApplicable must be false for {backend}");

            // A prior Auto-resolver run (or a later-phase re-check) sees the
            // already-resolved choice in the shared customizations instance.
            Assert.IsFalse(_step.IsApplicable(_lamcoCapableItem, null));
        }

        [TestMethod]
        public async Task ExecuteAsync_WaylandOnLamcoCapableDistro_ResolvesLamco()
        {
            SetupScriptOutput("RDP_DETECT=wayland");

            var c = new VmCustomizations { RdpBackend = RdpBackend.Auto };

            await _step.ExecuteAsync(_shell.Object, _lamcoCapableItem, c, _logger.Object, CancellationToken.None);

            Assert.AreEqual(RdpBackend.Lamco, c.RdpBackend, "Wayland default + Debian-family distro → Lamco");
        }

        [TestMethod]
        public async Task ExecuteAsync_X11_ResolvesXrdp()
        {
            SetupScriptOutput("RDP_DETECT=x11");
            var c = new VmCustomizations { RdpBackend = RdpBackend.Auto };

            await _step.ExecuteAsync(_shell.Object, _lamcoCapableItem, c, _logger.Object, CancellationToken.None);

            Assert.AreEqual(RdpBackend.Xrdp, c.RdpBackend);
        }

        [TestMethod]
        public async Task ExecuteAsync_MissingDetectLine_ResolvesXrdpWithWarning()
        {
            // Zero exit + no RDP_DETECT line = the script was swallowed. The
            // resolver never fails the deployment: it logs a warning and
            // routes conservatively to xrdp.
            SetupScriptOutput("some output without the contract line");
            var c = new VmCustomizations { RdpBackend = RdpBackend.Auto };

            await _step.ExecuteAsync(_shell.Object, _lamcoCapableItem, c, _logger.Object, CancellationToken.None);

            Assert.AreEqual(RdpBackend.Xrdp, c.RdpBackend);
            Assert.IsTrue(LoggedWarning(_logger), "a missing detection line must produce a loud warning");
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptTransportFailure_ResolvesXrdpWithWarning()
        {
            // Even a throwing shell (SSH hiccup mid-script) must not abort
            // the deployment — xrdp remains installable afterwards.
            _shell.Setup(s => s.RunCommandAsync(
                    It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("ssh transport died"));

            var c = new VmCustomizations { RdpBackend = RdpBackend.Auto };

            await _step.ExecuteAsync(_shell.Object, _lamcoCapableItem, c, _logger.Object, CancellationToken.None);

            Assert.AreEqual(RdpBackend.Xrdp, c.RdpBackend);
            Assert.IsTrue(LoggedWarning(_logger));
        }

        [TestMethod]
        public async Task ExecuteAsync_WaylandOnLamcoIncapableDistro_ResolvesXrdp()
        {
            // Wayland alone is not enough: Lamco ships Debian-family debs
            // only. Fedora/KDE is Wayland by default but not Lamco-capable.
            SetupScriptOutput("RDP_DETECT=wayland");
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("ID=fedora\n");
            var item = new GalleryItem { LinuxDistro = LinuxDistro.Fedora };
            var c = new VmCustomizations { RdpBackend = RdpBackend.Auto };

            await _step.ExecuteAsync(_shell.Object, item, c, _logger.Object, CancellationToken.None);

            Assert.AreEqual(RdpBackend.Xrdp, c.RdpBackend, "Wayland is not enough without a Lamco-capable distro");
        }

        [TestMethod]
        public async Task ExecuteAsync_DistroReadFailure_ResolvesXrdpWithWarning()
        {
            // /etc/os-release unreadable after the resolver's retry: no
            // Lamco verdict possible → xrdp with a loud warning (Lamco has
            // no business on a guest we cannot even read).
            SetupScriptOutput("RDP_DETECT=wayland");
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("ssh read failed"));
            var c = new VmCustomizations { RdpBackend = RdpBackend.Auto };

            await _step.ExecuteAsync(_shell.Object, _lamcoCapableItem, c, _logger.Object, CancellationToken.None);

            Assert.AreEqual(RdpBackend.Xrdp, c.RdpBackend);
            Assert.IsTrue(LoggedWarning(_logger));
        }

        [TestMethod]
        public async Task ExecuteAsync_OperationCanceled_IsRethrown()
        {
            // Cancellation is the one rethrow: the host's shutdown request
            // must propagate — a swallowed cancel would fabricate an xrdp
            // verdict on a dead deployment.
            var cts = new CancellationTokenSource();
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Throws(new OperationCanceledException(cts.Token));

            var c = new VmCustomizations { RdpBackend = RdpBackend.Auto };

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                _step.ExecuteAsync(_shell.Object, _lamcoCapableItem, c, _logger.Object, CancellationToken.None));

            // No verdict may be recorded before the cancel.
            Assert.AreEqual(RdpBackend.Auto, c.RdpBackend, "cancellation mid-run leaves the backend unresolved");
        }
    }
}