using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public sealed class EnableGraphicalAutologinStepTests
    {
        private EnableGraphicalAutologinStep _step;
        private Mock<IGuestShell> _shell;
        private Mock<ILogger<EnableGraphicalAutologinStep>> _logger;
        private GalleryItem _item;
        private VmCustomizations _lamcoCustomizations;
        private VmCustomizations _xrdpCustomizations;

        [TestInitialize]
        public void Setup()
        {
            _step = new EnableGraphicalAutologinStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<EnableGraphicalAutologinStep>>();
            _item = new GalleryItem { LinuxDistro = LinuxDistro.Ubuntu, InitialUsername = "ubuntu" };
            _lamcoCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Lamco };
            _xrdpCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Xrdp };
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Enable Graphical Autologin (Wayland)", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(238, _step.Order);
            Assert.AreEqual("Sub_EnableAutologin", _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_TrueForLamco()
        {
            Assert.IsTrue(_step.IsApplicable(_item, _lamcoCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForXrdp()
        {
            // xrdp deliberately disables autologin (display :0 at greeter, xrdp owns :10).
            Assert.IsFalse(_step.IsApplicable(_item, _xrdpCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForNone()
        {
            var c = new VmCustomizations { RdpBackend = RdpBackend.None };
            Assert.IsFalse(_step.IsApplicable(_item, c));
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysAndRunsScript()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("wayland-sessions") &&
                    content.Contains("AutomaticLogin") &&
                    content.Contains("enable-linger") &&
                    content.Contains("99-lamco-autologin")),
                "/tmp/enable_autologin.sh",
                It.IsAny<CancellationToken>()), Times.Once);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo bash /tmp/enable_autologin.sh") && cmd.Contains("sudo rm -f /tmp/enable_autologin.sh")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_SubstitutesAutologinUser()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => content.Contains("USER=\"ubuntu\"")),
                "/tmp/enable_autologin.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}