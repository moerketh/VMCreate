using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Mirrors <see cref="InstallLamcoRdpStepTests"/> for the wayrdp backend.
    /// wayrdp is delivered as a locally-built package tarball that the step
    /// scp's over SSH (the wayrdp repo is private, so no curl-from-GitHub like
    /// the Lamco fork). These tests lock the surrounding contracts the E2E
    /// pipeline relies on: gating, ordering, package+sha256+script delivery,
    /// /tmp hardening, the WAYRDP_RESULT result-line contract, runtime distro
    /// re-verification, and username hygiene.
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
            // wayrdp's supported set tracks the Lamco set (Debian-family +
            // Arch-family): freerdp3 runtime libs are packaged on both.
            foreach (var distro in new[] { LinuxDistro.Ubuntu, LinuxDistro.Debian, LinuxDistro.Parrot, LinuxDistro.Kali })
            {
                var item = new GalleryItem { LinuxDistro = distro };
                Assert.IsTrue(_step.IsApplicable(item, _wayrdpCustomizations),
                    $"{distro} should be wayrdp-targetable");
            }
        }

        [TestMethod]
        public void IsApplicable_TrueForArchFamily()
        {
            var item = new GalleryItem { LinuxDistro = LinuxDistro.Arch };
            Assert.IsTrue(_step.IsApplicable(item, _wayrdpCustomizations),
                "Arch (Omarchy/Hyprland) is the wlroots backend's primary target");
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

        /// <summary>
        /// Creates a temporary host tarball acting as the wayrdp package
        /// (content irrelevant — the step only scp's it + sha256's it).
        /// Pointed at by _wayrdpCustomizations.WayrdpPackagePath so the
        /// package-resolution guard passes. Returns the temp path.
        /// </summary>
        private string CreateHostTarball()
        {
            string path = Path.Combine(Path.GetTempPath(), $"wayrdp-test-{Guid.NewGuid():N}.tar.gz");
            File.WriteAllBytes(path, new byte[] { 0x1F, 0x8B, 1, 2, 3 }); // arbitrary gzip-ish bytes
            _wayrdpCustomizations.WayrdpPackagePath = path;
            return path;
        }

        [TestMethod]
        public async Task ExecuteAsync_MissingPackagePath_ThrowsBeforeCopy()
        {
            // WayrdpPackagePath unset → the step must fail on the host with a
            // clear directive, never touching the guest.
            _wayrdpCustomizations.WayrdpPackagePath = null;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _supportedItem, _wayrdpCustomizations, _logger.Object, CancellationToken.None));

            _shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _shell.Verify(s => s.CopyFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_PackageFileNotFound_ThrowsBeforeCopy()
        {
            _wayrdpCustomizations.WayrdpPackagePath = Path.Combine(Path.GetTempPath(), "does-not-exist-wayrdp.tar.gz");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _supportedItem, _wayrdpCustomizations, _logger.Object, CancellationToken.None));

            _shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_UploadsPackageAndScript_ThenRunsWithTmpHardening()
        {
            CreateHostTarball();
            // Success path: the script prints the terminal result line and
            // exits zero.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("...install output...\nWAYRDP_RESULT=ok\n");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _wayrdpCustomizations, _logger.Object, CancellationToken.None);

            // Script uploaded to an unpredictable /tmp path...
            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => content.Contains("wayrdp") && content.Contains("WAYRDP")),
                It.Is<string>(p => p.StartsWith("/tmp/install_wayrdp_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);

            // ...the tarball is scp'd from the host path to an unpredictable
            // per-deploy /tmp path (same /tmp hardening rationale as the
            // script name — concurrent deploys must never read or clobber
            // each other's package files)...
            _shell.Verify(s => s.CopyFileAsync(
                It.Is<string>(h => h == _wayrdpCustomizations.WayrdpPackagePath),
                It.Is<string>(g => g.StartsWith("/tmp/wayrdp-pkg_") && g.EndsWith(".tar.gz")),
                It.IsAny<CancellationToken>()), Times.Once);

            // ...the sha256 sidecar sits next to its tarball with the matching
            // (cosmetic) filename in the digest line...
            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => Regex.IsMatch(content.TrimEnd(), @"^\S+  wayrdp-pkg_[0-9a-f]{32}\.tar\.gz$")),
                It.Is<string>(g => g.StartsWith("/tmp/wayrdp-pkg_") && g.EndsWith(".tar.gz.sha256")),
                It.IsAny<CancellationToken>()), Times.Once);

            // ...and both per-deploy paths are injected into the script via
            // placeholders, so the script reads exactly the files that were
            // scp'd (never a predictable /tmp name).
            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => !content.Contains("__WAYRDP_PKG_TARBALL__")
                                         && !content.Contains("__WAYRDP_PKG_SHA256_FILE__")
                                         && content.Contains("WAYRDP_PKG_TARBALL=")),
                It.Is<string>(p => p.StartsWith("/tmp/install_wayrdp_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);

            // The run command hardens perms, captures the exit code so cleanup
            // ALWAYS runs (rm not gated behind the script's success), and the
            // script's sha256 placeholder is injected host-side.
            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo chown root:root /tmp/install_wayrdp_")
                                     && cmd.Contains("sudo chmod 0700 /tmp/install_wayrdp_")
                                     && cmd.Contains("sudo bash /tmp/install_wayrdp_")
                                     && cmd.Contains("rc=$?")
                                     && cmd.Contains("sudo rm -f")
                                     && cmd.Contains("exit $rc")),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_MissingResultLineOnZeroExit_Throws()
        {
            CreateHostTarball();
            // A completely swallowed install (zero exit, no terminal line) must
            // NOT report success.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("some output with no result line\n");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _supportedItem, _wayrdpCustomizations, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        public async Task ExecuteAsync_GuestFailure_PropagatesThrow()
        {
            CreateHostTarball();
            // Non-zero SSH exit → RunCommandAsync throws; the step must not
            // mask it (proves the failure isn't hidden by a later command).
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("SSH command failed: exit 1"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _supportedItem, _wayrdpCustomizations, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        public async Task ExecuteAsync_DegradedResult_LogsWarningNoThrow()
        {
            CreateHostTarball();
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("...\nWAYRDP_RESULT=degraded\n");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _wayrdpCustomizations, _logger.Object, CancellationToken.None);

            _logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("DEGRADED")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
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
