using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public sealed class FixXrdpStepTests
    {
        private FixXrdpStep _step;
        private Mock<IGuestShell> _shell;
        private Mock<ILogger<FixXrdpStep>> _logger;
        private GalleryItem _item;
        private VmCustomizations _customizations;

        [TestInitialize]
        public void Setup()
        {
            _step = new FixXrdpStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<FixXrdpStep>>();
            _item = new GalleryItem();
            _customizations = new VmCustomizations();
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Fix xrdp for Hyper-V", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(245, _step.Order);
            Assert.AreEqual("Sub_FixXrdp", _step.ProgressPhaseId);
            Assert.IsTrue(_step.IsApplicable(_item, _customizations));
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysAndRunsScript()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("/etc/X11/xrdp/xorg.conf") &&
                    content.Contains("/etc/xrdp/startwm.sh")),
                "/tmp/fix_xrdp.sh",
                It.IsAny<CancellationToken>()), Times.Once);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo bash /tmp/fix_xrdp.sh") && cmd.Contains("sudo rm -f /tmp/fix_xrdp.sh")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptFixesXrdpXorgConf()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("/etc/X11/xrdp/xorg.conf") &&
                    content.Contains("DRMDevice") &&
                    content.Contains("DRI3") &&
                    content.Contains("DRMAllowList") &&
                    content.Contains("xorg.conf.bak")),
                "/tmp/fix_xrdp.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptWritesXrdpStartwm()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("/etc/xrdp/startwm.sh") &&
                    content.Contains("KWIN_COMPOSE=N") &&
                    content.Contains("QT_QUICK_BACKEND=software") &&
                    content.Contains("QSG_RENDER_LOOP=basic") &&
                    content.Contains("XDG_SESSION_TYPE=x11") &&
                    content.Contains("/usr/bin/dbus-launch") &&
                    content.Contains("startplasma-x11") &&
                    content.Contains("startwm.sh.bak")),
                "/tmp/fix_xrdp.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptIsSafeNoOpWhenXrdpNotInstalled()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => content.Contains("if [ -d /etc/xrdp ]")),
                "/tmp/fix_xrdp.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptNormalizesLineEndings()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => !content.Contains("\r\n")),
                "/tmp/fix_xrdp.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public void IsApplicable_TrueExceptForLamcoBackend()
        {
            // xrdp-fix steps only apply to the xrdp path; Lamco (Wayland-native) skips them.
            Assert.IsTrue(_step.IsApplicable(null, null));
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations()));
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations { RdpBackend = RdpBackend.Xrdp }));
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations { RdpBackend = RdpBackend.None }));
            Assert.IsFalse(_step.IsApplicable(new GalleryItem(), new VmCustomizations { RdpBackend = RdpBackend.Lamco }));
        }
    }
}
