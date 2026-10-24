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
    public class InstallOpenVpnStepTests
    {
        private Mock<IGuestShell> _shellMock = null!;
        private Mock<ILogger> _loggerMock = null!;
        private InstallOpenVpnStep _step = null!;
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
            Assert.AreEqual("Sub_InstallOpenVpn", _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_WhenInstallOpenVpnTrue_ReturnsTrue()
        {
            var customizations = new VmCustomizations { InstallOpenVpn = true };
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), customizations));
        }

        [TestMethod]
        public void IsApplicable_WhenInstallOpenVpnFalse_ReturnsFalse()
        {
            var customizations = new VmCustomizations { InstallOpenVpn = false };
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
                    It.Is<string>(cmd => cmd.Contains("openvpn --version")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("OpenVPN 2.6\n");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations { InstallOpenVpn = true },
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("apt-get") || cmd.Contains("dnf")),
                It.IsAny<CancellationToken>()), Times.Once);

            _shellMock.Verify(s => s.RunCommandAsync(
                "sudo systemctl restart NetworkManager 2>&1 || true",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_InstallCommandNeverHardFails_EveryBranchNeutralisesExitStatus()
        {
            // The SSH automation path treats non-zero guest exits as fatal for
            // the whole deployment ("Unable to find a match: openvpn" exit 1
            // observed live on Rocky EL10 without EPEL). A convenience
            // install must never kill a VM build — every package-manager
            // branch must end in an exit-neutraliser.
            _shellMock.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("WARNING: openvpn packages unavailable");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations { InstallOpenVpn = true },
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => !cmd.Contains("exit 1") && !cmd.Contains("echo 'ERROR")),
                It.IsAny<CancellationToken>()), Times.AtLeast(3));
        }

        [TestMethod]
        public async Task ExecuteAsync_PacmanBranch_BypassesOmarchyUpdateGuard()
        {
            // Live (Omarchy 4.0.6): the 00-omarchy-update-guard hook aborts any
            // direct -Syu. The env must survive sudo, so `sudo env VAR=1`.
            _shellMock.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations { InstallOpenVpn = true },
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo env OMARCHY_ALLOW_DIRECT_PACMAN=1 pacman -Syu --noconfirm openvpn")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_OpenVpnMissingAfterInstall_LogsWarningAndCompletes()
        {
            const string dnfOutput =
                "dnf install -y -q openvpn NetworkManager-openvpn\nUnable to find a match: openvpn\nWARNING: openvpn packages unavailable in enabled dnf repos (EPEL may be required on this distro)";

            _shellMock
                .Setup(s => s.RunCommandAsync(
                    It.Is<string>(cmd => cmd.Contains("apt-get") || cmd.Contains("dnf")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(dnfOutput);

            _shellMock
                .Setup(s => s.RunCommandAsync(
                    It.Is<string>(cmd => cmd.Contains("openvpn --version")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("WARNING: openvpn binary not present after install");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations { InstallOpenVpn = true },
                _loggerMock.Object,
                CancellationToken.None);

            _loggerMock.Verify(
                l => l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("unavailable")),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
    }
}