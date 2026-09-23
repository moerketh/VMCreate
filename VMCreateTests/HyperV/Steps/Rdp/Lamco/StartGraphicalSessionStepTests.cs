using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Tests for <see cref="StartGraphicalSessionStep"/> — the no-reboot
    /// completion for Lamco deployments. Runs at order 239, immediately
    /// after <see cref="EnableGraphicalAutologinStep"/> (238): restarts
    /// the display manager so the just-written autologin config logs the
    /// desktop user in, activating graphical-session.target and the lamco
    /// units bound to it.
    /// </summary>
    [TestClass]
    public sealed class StartGraphicalSessionStepTests
    {
        private StartGraphicalSessionStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<StartGraphicalSessionStep>> _logger = null!;
        private GalleryItem _item = null!;
        private VmCustomizations _lamcoCustomizations = null!;
        private VmCustomizations _xrdpCustomizations = null!;
        private VmCustomizations _autoCustomizations = null!;

        [TestInitialize]
        public void Setup()
        {
            _step = new StartGraphicalSessionStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<StartGraphicalSessionStep>>();
            _item = new GalleryItem { LinuxDistro = LinuxDistro.Kali, InitialUsername = "kali" };
            _lamcoCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Lamco };
            _xrdpCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Xrdp };
            _autoCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Auto };
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Start Graphical Session", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            // AFTER EnableGraphicalAutologinStep (238): the DM restart is
            // only meaningful once the autologin config it activates
            // exists. Before the xrdp block (240+).
            Assert.AreEqual(239, _step.Order);
            Assert.AreEqual("Sub_StartGraphicalSession", _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_TrueForLamcoOnDebianFamily()
        {
            Assert.IsTrue(_step.IsApplicable(_item, _lamcoCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForRpmDistros()
        {
            // Same gate as the other Lamco steps: lamco installs Debian-family
            // debs only; a Fedora item with backend=Lamco is a misconfiguration
            // this step must not paper over with a DM restart.
            var fedoraItem = new GalleryItem { LinuxDistro = LinuxDistro.Fedora };
            Assert.IsFalse(_step.IsApplicable(fedoraItem, _lamcoCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForXrdp()
        {
            // xrdp creates its session at connect time and deliberately keeps
            // the greeter on :0 with autologin OFF — restarting its DM would
            // wedge nothing useful and can break the greeter the user expects.
            Assert.IsFalse(_step.IsApplicable(_item, _xrdpCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForAuto()
        {
            // Under Auto the backend is not yet resolved when steps are
            // evaluated pre-run; the resolver mutates customizations
            // in place at 232, and this step (239) only sees Lamco after
            // that. A still-Auto customizations object must not trigger a
            // DM restart (it would run under xrdp too).
            Assert.IsFalse(_step.IsApplicable(_item, _autoCustomizations));
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultOk_Succeeds()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("Restarting display-manager.service...\nSESSION_START_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.IsAny<string>(),
                It.Is<string>(p => p.StartsWith("/tmp/start_graphical_session_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);
            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("chown root:root") && cmd.Contains("chmod 0700") && cmd.Contains("rm -f")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultConsent_LogsInfoNotWarning()
        {
            // "consent" is the expected first-boot verdict: deployment
            // complete and correct, one Allow click on the console pending.
            // It must log at Information (a Warning would train operators
            // to ignore the deploy's warnings for a normal condition).
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("Graphical session active; one-time portal consent dialog on console.\nSESSION_START_RESULT=consent");

            await _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _logger.Verify(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("consent")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
            _logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultDegraded_LogsWarning()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("DEGRADED: readiness gate timed out.\nSESSION_START_RESULT=degraded");

            await _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("DEGRADED")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Missing_Throws()
        {
            // Zero exit with no SESSION_START_RESULT line = fabricated
            // success — must throw (same contract as every Lamco step).
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("some output but no terminal contract line");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        public async Task ExecuteAsync_SubstitutesAutologinUser()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("SESSION_START_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => content.Contains("USER=\"kali\"")),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_InvalidUsername_ThrowsBeforeScriptCopy()
        {
            // A gallery field that is not a valid Linux username must never
            // reach the root-run script (same guard as InstallLamcoRdpStep).
            var badItem = new GalleryItem { LinuxDistro = LinuxDistro.Kali, InitialUsername = "kali;rm -rf /" };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, badItem, _lamcoCustomizations, _logger.Object, CancellationToken.None));

            _shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptCarriesDmRestartAndHardeningMarkers()
        {
            // The shipped script must contain the DM restart (the whole
            // point of the step) and the hardened invocation pattern.
            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, path, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("SESSION_START_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(copiedContent);
            StringAssert.Contains(copiedContent, "systemctl restart", "restarts the DM to activate the session");
            StringAssert.Contains(copiedContent, "graphical-session.target", "waits for the lamco units' activation target");
            StringAssert.Contains(copiedContent, "SESSION_START_RESULT=", "carries the result-line contract");
            StringAssert.Contains(copiedContent, "'__AUTOLOGIN'", "sentinel built from fragments so the host Replace cannot self-clobber");
        }
    }
}