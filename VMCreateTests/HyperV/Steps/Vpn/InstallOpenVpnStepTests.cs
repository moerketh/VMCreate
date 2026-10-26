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
        public async Task ExecuteAsync_PacmanBranch_InstallsOnlyRequestedPackages_WithoutGuardBypassOrUpgrade()
        {
            // The distro author's 00-omarchy-update-guard hook deliberately
            // aborts direct -Syu transactions; the step must not bypass it or
            // silently upgrade the whole system. It installs only the two
            // requested packages against the existing sync DBs.
            _shellMock.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations { InstallOpenVpn = true },
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo pacman -S --needed --noconfirm openvpn networkmanager-openvpn")),
                It.IsAny<CancellationToken>()), Times.Once);
            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("-Syu") || cmd.Contains("OMARCHY_ALLOW_DIRECT_PACMAN")),
                It.IsAny<CancellationToken>()), Times.Never);
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

        [TestMethod]
        public async Task ExecuteAsync_InstallCommand_PerformsNoRepoTrustChangesOrSystemUpgrades()
        {
            // Regression: the install command must not silently enable EPEL
            // (a new signing authority for the guest's VM life) and must not
            // bypass the distro's update guard with a full pacman -Syu
            // upgrade. It only installs the requested packages and degrades
            // to an owner-visible warning when unavailable.
            string? firstCommand = null;
            _shellMock
                .Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((cmd, _) => firstCommand ??= cmd)
                .ReturnsAsync("OpenVPN plugin present for NetworkManager");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations { InstallOpenVpn = true },
                _loggerMock.Object,
                CancellationToken.None);

            Assert.IsNotNull(firstCommand, "the install command must be the first guest command");
            Assert.IsFalse(firstCommand.Contains("epel-release"), "must not install EPEL as a side effect");
            Assert.IsFalse(firstCommand.Contains("--set-enabled"), "must not enable repositories as a side effect");
            Assert.IsFalse(firstCommand.Contains("-Syu"), "must not perform a full system upgrade");
            Assert.IsFalse(firstCommand.Contains("OMARCHY_ALLOW_DIRECT_PACMAN"), "must not bypass the distro update guard");
            Assert.IsTrue(firstCommand.Contains("pacman -S --needed"), "pacman branch must be a plain install");
        }

        [TestMethod]
        public async Task ExecuteAsync_VerifyChecksNetworkManagerPlugin_NotJustTheBinary()
        {
            // Regression: a preinstalled openvpn binary must not mask a failed
            // plugin transaction — the verify step must look for the
            // NetworkManager plugin artifact the later nmcli import requires.
            string? verifyCommand = null;
            _shellMock
                .Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((cmd, _) =>
                {
                    if (cmd.Contains("compgen")) verifyCommand = cmd;
                })
                .ReturnsAsync("OpenVPN plugin present for NetworkManager");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations { InstallOpenVpn = true },
                _loggerMock.Object,
                CancellationToken.None);

            Assert.IsNotNull(verifyCommand, "a plugin-presence verify must run");
            StringAssert.Contains(verifyCommand, "lib*openvpn*.so*", "the verify must probe the NM plugin library");
        }
    }
}