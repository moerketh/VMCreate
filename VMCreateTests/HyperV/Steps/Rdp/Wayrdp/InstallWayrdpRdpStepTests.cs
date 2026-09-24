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
    /// Mirrors <see cref="InstallLamcoRdpStepTests"/> for the wayrdp backend.
    /// The wayrdp placeholder install script always fails loudly in the guest
    /// (Phase 0: no artifact); these tests lock the surrounding contracts that
    /// the E2E pipeline relies on: gating, ordering, script deployment with
    /// /tmp hardening, runtime distro re-verification, and username hygiene.
    /// </summary>
    [TestClass]
    public sealed class InstallWayrdpRdpStepTests
    {
        private InstallWayrdpRdpStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<InstallWayrdpRdpStep>> _logger = null!;
        private GalleryItem _supportedItem = null!;
        private GalleryItem _unsupportedItem = null!;
        private VmCustomizations _wayrdpCustomizations = null!;
        private VmCustomizations _lamcoCustomizations = null!;

        [TestInitialize]
        public void Setup()
        {
            _step = new InstallWayrdpRdpStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            // Runtime distro re-verification (shared with the Lamco path):
            // default to a Debian-family guest.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("ID=parrot\nID_LIKE=debian\n");
            _logger = new Mock<ILogger<InstallWayrdpRdpStep>>();
            _supportedItem = new GalleryItem { LinuxDistro = LinuxDistro.Ubuntu, InitialUsername = "testuser" };
            _unsupportedItem = new GalleryItem { LinuxDistro = LinuxDistro.Unknown };
            _wayrdpCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Wayrdp };
            _lamcoCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Lamco };
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Install wayrdp RDP Server", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            // 237: after the Lamco install slot (235) and the xrdp post-boot
            // slot (236), before autologin (238) / session start (239).
            Assert.AreEqual(237, _step.Order);
            Assert.AreEqual("Sub_InstallWayrdpRdp", _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_TrueForWayrdpOnSupportedDistro()
        {
            Assert.IsTrue(_step.IsApplicable(_supportedItem, _wayrdpCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForLamcoEvenOnSupportedDistro()
        {
            Assert.IsFalse(_step.IsApplicable(_supportedItem, _lamcoCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForWayrdpOnUnsupportedDistro()
        {
            Assert.IsFalse(_step.IsApplicable(_unsupportedItem, _wayrdpCustomizations));
        }

        [TestMethod]
        public void IsApplicable_TrueForAllDebianFamilyDistros()
        {
            // wayrdp's packaging line follows the same amd64 Debian-deb
            // lineage as the Lamco fork.
            foreach (var distro in new[] { LinuxDistro.Ubuntu, LinuxDistro.Debian, LinuxDistro.Parrot, LinuxDistro.Kali })
            {
                var item = new GalleryItem { LinuxDistro = distro };
                Assert.IsTrue(_step.IsApplicable(item, _wayrdpCustomizations),
                    $"{distro} should be wayrdp-targetable");
            }
        }

        [TestMethod]
        public void IsApplicable_FalseForRpmDistros()
        {
            foreach (var distro in new[] { LinuxDistro.Fedora, LinuxDistro.OpenSuse, LinuxDistro.Unknown })
            {
                var item = new GalleryItem { LinuxDistro = distro };
                Assert.IsFalse(_step.IsApplicable(item, _wayrdpCustomizations),
                    $"{distro} must not be offered the wayrdp backend");
            }
        }

        [TestMethod]
        public async Task ExecuteAsync_InvalidInitialUsername_ThrowsBeforeCopy()
        {
            var item = new GalleryItem { LinuxDistro = LinuxDistro.Parrot, InitialUsername = "user; rm -rf /" };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, item, _wayrdpCustomizations, _logger.Object, CancellationToken.None));

            _shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysAndRunsScript_WithTmpHardening()
        {
            // The placeholder script exits non-zero in the guest; the mock
            // simulates the throw that RunCommandAsync would raise on
            // non-zero SSH exit, proving the failure propagates (is not
            // masked by a later command using ";").
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("SSH command failed: exit 1"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _supportedItem, _wayrdpCustomizations, _logger.Object, CancellationToken.None));

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => content.Contains("wayrdp") && content.Contains("WAYRDP")),
                It.Is<string>(p => p.StartsWith("/tmp/install_wayrdp_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo chown root:root /tmp/install_wayrdp_")
                                     && cmd.Contains("sudo chmod 0700 /tmp/install_wayrdp_")
                                     && cmd.Contains("&& sudo bash /tmp/install_wayrdp_")
                                     && cmd.Contains("&& sudo rm -f /tmp/install_wayrdp_")),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_RuntimeDistroUnsupported_Throws()
        {
            // Gallery hint said Debian-family but the guest's os-release
            // disagrees — the step must fail on the host, not inside a
            // root-run script (same contract as InstallLamcoRdpStep).
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("ID=fedora\n");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _supportedItem, _wayrdpCustomizations, _logger.Object, CancellationToken.None));

            _shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
