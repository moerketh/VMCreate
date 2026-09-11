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
    public sealed class ForceX11StepTests
    {
        private ForceX11Step _step;
        private Mock<IGuestShell> _shell;
        private Mock<ILogger<ForceX11Step>> _logger;
        private GalleryItem _item;
        private VmCustomizations _customizations;

        [TestInitialize]
        public void Setup()
        {
            _step = new ForceX11Step();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<ForceX11Step>>();
            _item = new GalleryItem();
            _customizations = new VmCustomizations();
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Force X11 Display Server", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(240, _step.Order);
            Assert.AreEqual("Sub_ForceX11", _step.ProgressPhaseId);
            Assert.IsTrue(_step.IsApplicable(_item, _customizations));
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysAndRunsScript()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("wayland-sessions") &&
                    content.Contains("DisplayServer=x11")),
                "/tmp/force_x11.sh",
                It.IsAny<CancellationToken>()), Times.Once);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo bash /tmp/force_x11.sh") && cmd.Contains("sudo rm -f /tmp/force_x11.sh")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptRestoresWaylandSessions()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("disabled/*.desktop") &&
                    content.Contains("Restored previously disabled Wayland sessions")),
                "/tmp/force_x11.sh",
                It.IsAny<CancellationToken>()), Times.Once);

            var scriptContent = _shell.Invocations
                .First(i => i.Method.Name == "CopyContentAsync")
                .Arguments[0] as string;
            Assert.IsTrue(scriptContent.Contains(".desktop.disabled"),
                "Script should handle .desktop.disabled files from older scripts");
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptConfiguresSDDM()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("/etc/sddm.conf.d/force-x11.conf") &&
                    content.Contains("DisplayServer=x11")),
                "/tmp/force_x11.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptConfiguresLightDM()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("/etc/lightdm/lightdm.conf.d/91-hyperv-x11.conf") &&
                    content.Contains("[Seat:*]") &&
                    content.Contains("user-session") &&
                    content.Contains("autologin-user=user") &&
                    content.Contains("autologin-user-timeout=0")),
                "/tmp/force_x11.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptConfiguresGDM()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("/etc/gdm3/custom.conf") &&
                    content.Contains("/etc/gdm/custom.conf") &&
                    content.Contains("WaylandEnable=false")),
                "/tmp/force_x11.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptAutoDetectsX11Session()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("/usr/share/xsessions/*.desktop") &&
                    content.Contains("x11_session")),
                "/tmp/force_x11.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptInstallsDbusX11()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("dbus-x11") &&
                    content.Contains("apt-get install -y dbus-x11")),
                "/tmp/force_x11.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptFixesXwrapperConfig()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("/etc/X11/Xwrapper.config") &&
                    content.Contains("allowed_users=anybody") &&
                    content.Contains("Xwrapper.config.bak")),
                "/tmp/force_x11.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptDoesNotContainExtractedSteps()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    !content.Contains("/etc/X11/xrdp/xorg.conf") &&
                    !content.Contains("/etc/xrdp/startwm.sh") &&
                    !content.Contains(".config/kwinrc") &&
                    !content.Contains("/var/lib/AccountsService/users") &&
                    !content.Contains("Wayland sessions disabled") &&
                    !content.Contains("blacklist-hyperv.conf") &&
                    !content.Contains("hyperv-daemons") &&
                    !content.Contains("update-initramfs")),
                "/tmp/force_x11.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptNormalizesLineEndings()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => !content.Contains("\r\n")),
                "/tmp/force_x11.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public void IsApplicable_TrueExceptForLamcoBackend()
        {
            // Default (Xrdp) and None still force X11 for Hyper-V hyperv_drm stability;
            // only the Wayland-native Lamco backend skips this step.
            Assert.IsTrue(_step.IsApplicable(null, null));
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations()));
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations { RdpBackend = RdpBackend.Xrdp }));
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations { RdpBackend = RdpBackend.None }));
            Assert.IsFalse(_step.IsApplicable(new GalleryItem(), new VmCustomizations { RdpBackend = RdpBackend.Lamco }));
        }
    }
}
