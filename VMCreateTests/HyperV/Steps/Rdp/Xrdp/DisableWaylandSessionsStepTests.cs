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
    [TestClass]
    public sealed class DisableWaylandSessionsStepTests
    {
        private DisableWaylandSessionsStep _step;
        private Mock<IGuestShell> _shell;
        private Mock<ILogger<DisableWaylandSessionsStep>> _logger;
        private GalleryItem _item;
        private VmCustomizations _customizations;

        [TestInitialize]
        public void Setup()
        {
            _step = new DisableWaylandSessionsStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<DisableWaylandSessionsStep>>();
            _item = new GalleryItem();
            _customizations = new VmCustomizations();
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Disable Wayland Sessions", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(270, _step.Order);
            Assert.AreEqual("Sub_DisableWaylandSessions", _step.ProgressPhaseId);
            Assert.IsTrue(_step.IsApplicable(_item, _customizations));
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysAndRunsScript()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => content.Contains("wayland-sessions")),
                "/tmp/disable_wayland.sh",
                It.IsAny<CancellationToken>()), Times.Once);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo bash /tmp/disable_wayland.sh") && cmd.Contains("sudo rm -f /tmp/disable_wayland.sh")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptRestoresPreviouslyDisabledSessions()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("disabled/*.desktop") &&
                    content.Contains("Restored previously disabled Wayland sessions")),
                "/tmp/disable_wayland.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptDisablesWaylandSessions()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("mkdir -p /usr/share/wayland-sessions/disabled") &&
                    content.Contains("/usr/share/wayland-sessions/*.desktop") &&
                    content.Contains("Wayland sessions disabled")),
                "/tmp/disable_wayland.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptRestoresBeforeDisabling()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            var content = _shell.Invocations
                .First(i => i.Method.Name == "CopyContentAsync")
                .Arguments[0] as string;

            int restoreIndex = content.IndexOf("Restored previously disabled", StringComparison.Ordinal);
            int disableIndex = content.IndexOf("Wayland sessions disabled", StringComparison.Ordinal);

            Assert.IsTrue(restoreIndex < disableIndex, "Restore step must come before disable step");
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptUnblocksHypervModules()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => content.Contains("blacklist-hyperv.conf")),
                "/tmp/disable_wayland.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptHandlesDesktopDisabledFiles()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => content.Contains(".desktop.disabled")),
                "/tmp/disable_wayland.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptNormalizesLineEndings()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => !content.Contains("\r\n")),
                "/tmp/disable_wayland.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public void IsApplicable_TrueExceptForLamcoBackend()
        {
            // Wayland sessions are disabled for the xrdp/X11 path; Lamco (Wayland-native) keeps them.
            Assert.IsTrue(_step.IsApplicable(null, null));
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations()));
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations { RdpBackend = RdpBackend.Xrdp }));
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations { RdpBackend = RdpBackend.None }));
            Assert.IsFalse(_step.IsApplicable(new GalleryItem(), new VmCustomizations { RdpBackend = RdpBackend.Lamco }));
        }
    }
}
