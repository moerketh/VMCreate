using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public class InstallOpenVpnStepTests
    {
        private Mock<IGuestShell> _shellMock;
        private Mock<ILogger> _loggerMock;
        private InstallOpenVpnStep _step;

        [TestInitialize]
        public void Setup()
        {
            _shellMock = new Mock<IGuestShell>();
            _loggerMock = new Mock<ILogger>();
            _step = new InstallOpenVpnStep();
        }

        [TestMethod]
        public void Metadata_IsCorrect()
        {
            Assert.AreEqual("Install OpenVPN", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(200, _step.Order);
            Assert.AreEqual("Sub_ConfigureVpn", _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_WhenConfigureHtbVpnTrue_ReturnsTrue()
        {
            var customizations = new VmCustomizations { ConfigureHtbVpn = true };
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), customizations));
        }

        [TestMethod]
        public void IsApplicable_WhenConfigureHtbVpnFalse_ReturnsFalse()
        {
            var customizations = new VmCustomizations { ConfigureHtbVpn = false };
            Assert.IsFalse(_step.IsApplicable(new GalleryItem(), customizations));
        }

        [TestMethod]
        public async Task ExecuteAsync_RunsInstallCommandAndRestartsNetworkManager()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(
                    It.Is<string>(cmd => cmd.Contains("apt-get") || cmd.Contains("dnf")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("installed");

            _shellMock
                .Setup(s => s.RunCommandAsync("sudo systemctl restart NetworkManager 2>&1 || true", It.IsAny<CancellationToken>()))
                .ReturnsAsync("");

            _shellMock
                .Setup(s => s.RunCommandAsync(
                    "command -v openvpn || /usr/sbin/openvpn --version 2>&1 | head -1",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("OpenVPN 2.6");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations { ConfigureHtbVpn = true },
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("apt-get") || cmd.Contains("dnf")),
                It.IsAny<CancellationToken>()), Times.Once);

            _shellMock.Verify(s => s.RunCommandAsync(
                "sudo systemctl restart NetworkManager 2>&1 || true",
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
