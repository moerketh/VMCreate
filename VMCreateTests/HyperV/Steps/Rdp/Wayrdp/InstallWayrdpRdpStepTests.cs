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
    /// The package is fetched guest-side: the install script curls the pinned
    /// public release asset and verifies it against the sha256 pinned in the
    /// script itself (same pinned-download contract as the Lamco fork's deb).
    /// These tests lock the surrounding contracts the E2E pipeline relies on:
    /// gating, ordering, script-only delivery, /tmp hardening, the
    /// WAYRDP_RESULT result-line contract, runtime distro re-verification,
    /// and username hygiene.
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

        [TestMethod]
        public async Task ExecuteAsync_UploadsScriptOnly_ThenRunsWithTmpHardening()
        {
            // Success path: the script prints the terminal result line and
            // exits zero.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("...install output...\nWAYRDP_RESULT=ok\n");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _wayrdpCustomizations, _logger.Object, CancellationToken.None);

            // The script — and ONLY the script — is uploaded, to an
            // unpredictable /tmp path: the package download+verify happens
            // guest-side against the constants pinned inside the script, so
            // no package file is ever scp'd from the host.
            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => content.Contains("WAYRDP_TARBALL_URL")
                                         && content.Contains("releases/latest/download/")
                                         && content.Contains("b7d88525dad6e7542865a6b389d1ff91c685d9c2cae035f80928a4bc7bcaa9d0")
                                         && !content.Contains("__WAYRDP_PKG")),
                It.Is<string>(p => p.StartsWith("/tmp/install_wayrdp_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);
            _shell.Verify(s => s.CopyFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            // The run command hardens perms, captures the exit code so cleanup
            // ALWAYS runs (rm not gated behind the script's success) — and
            // cleans up only the script (no host-side tarball exists).
            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo chown root:root /tmp/install_wayrdp_")
                                     && cmd.Contains("sudo chmod 0700 /tmp/install_wayrdp_")
                                     && cmd.Contains("sudo bash /tmp/install_wayrdp_")
                                     && cmd.Contains("rc=$?")
                                     && cmd.Contains("sudo rm -f")
                                     && cmd.Contains("exit $rc")
                                     // exactly one file removed in the rm
                                     && !cmd.Contains(".tar.gz")),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptWritesHyprlandStartHook()
        {
            // graphical-session.target never activates on Omarchy; without
            // an exec_on_start hook wayrdp.service sat "inactive (dead)".
            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, path, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("WAYRDP_RESULT=ok\n");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _wayrdpCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(copiedContent);
            StringAssert.Contains(copiedContent, "vmcreate wayrdp autostart", "marker keeps the hook idempotent");
            StringAssert.Contains(copiedContent, "o.exec_on_start(\"systemctl --user start wayrdp.service\")");
            StringAssert.Contains(copiedContent, "dbus-update-activation-environment --systemd WAYLAND_DISPLAY",
                "the unit's ConditionEnvironment=WAYLAND_DISPLAY needs the session env imported first");
            StringAssert.Contains(copiedContent, "NOTICE: wayrdp.service not active yet",
                "no session yet at install time is expected (step 239 starts it) — a NOTICE, not DEGRADED");
        }

        [TestMethod]
        public async Task ExecuteAsync_MissingResultLineOnZeroExit_Throws()
        {
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
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("...\nWAYRDP_RESULT=degraded\n");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _wayrdpCustomizations, _logger.Object, CancellationToken.None);

            _logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("DEGRADED")),
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
