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
    public sealed class InstallLamcoRdpStepTests
    {
        private InstallLamcoRdpStep _step;
        private Mock<IGuestShell> _shell;
        private Mock<ILogger<InstallLamcoRdpStep>> _logger;
        private GalleryItem _supportedItem;
        private GalleryItem _unsupportedItem;
        private VmCustomizations _lamcoCustomizations;
        private VmCustomizations _xrdpCustomizations;

        [TestInitialize]
        public void Setup()
        {
            _step = new InstallLamcoRdpStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<InstallLamcoRdpStep>>();
            _supportedItem = new GalleryItem { LinuxDistro = LinuxDistro.Ubuntu };
            _unsupportedItem = new GalleryItem { LinuxDistro = LinuxDistro.Unknown };
            _lamcoCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Lamco };
            _xrdpCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Xrdp };
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Install Lamco RDP Server", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(235, _step.Order);
            Assert.AreEqual("Sub_InstallLamcoRdp", _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_TrueForLamcoOnSupportedDistro()
        {
            Assert.IsTrue(_step.IsApplicable(_supportedItem, _lamcoCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForXrdpEvenOnSupportedDistro()
        {
            Assert.IsFalse(_step.IsApplicable(_supportedItem, _xrdpCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForLamcoOnUnsupportedDistro()
        {
            Assert.IsFalse(_step.IsApplicable(_unsupportedItem, _lamcoCustomizations));
        }

        [TestMethod]
        public void IsApplicable_TrueForAllPoCSupportedDistros()
        {
            foreach (var distro in new[] { LinuxDistro.Ubuntu, LinuxDistro.Fedora, LinuxDistro.Debian, LinuxDistro.OpenSuse, LinuxDistro.Parrot })
            {
                var item = new GalleryItem { LinuxDistro = distro };
                Assert.IsTrue(_step.IsApplicable(item, _lamcoCustomizations),
                    $"{distro} should be supported");
            }
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysAndRunsScript()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("lamco-rdp-server") &&
                    content.Contains("/etc/os-release") &&
                    content.Contains("config.toml") &&
                    content.Contains("lamco-rdp-server.service")),
                "/tmp/install_lamco.sh",
                It.IsAny<CancellationToken>()), Times.Once);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo bash /tmp/install_lamco.sh") && cmd.Contains("sudo rm -f /tmp/install_lamco.sh")),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_UsesAndNotSemicolon_SoScriptFailurePropagates()
        {
            // Regression guard: the command must use "&&" (not ";") between the script
            // invocation and the cleanup, so a non-zero exit code from the script is
            // returned to SSH and surfaces as a deployment failure instead of being
            // masked by the always-succeeding rm.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo bash /tmp/install_lamco.sh && sudo rm -f /tmp/install_lamco.sh")
                                     && !cmd.Contains(".sh; sudo")),
                It.Is<TimeSpan>(t => t >= TimeSpan.FromMinutes(15)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptFailure_ThrowsAndIsNotSwallowed()
        {
            // Regression guard for the TEST VM incident where a bash syntax error in the
            // embedded script was swallowed (GUI reported success, no Lamco installed).
            // The step must let the SSH exception propagate so the orchestrator reports failure.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new Exception("SSH command failed (exit code 2): syntax error"));

            await Assert.ThrowsAsync<Exception>(() =>
                _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        public async Task ExecuteAsync_ProvisionsTransparentCursorTheme_ForKde()
        {
            // xrdp-parity: the guest cursor must never be composited into the
            // captured video (KWin bakes it in on Hyper-V's software cursor
            // plane, creating a lagging "ghost" arrow behind the client-side
            // pointer). The install script must generate + install the
            // transparent XCursor theme and activate it for the autologin user.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            // Generated + installed under the system icon path (KDE-gated by kwriteconfig presence)
            StringAssert.Contains(captured, "kwriteconfig6", "KDE detection gate");
            StringAssert.Contains(captured, "/usr/share/icons/transparent", "theme install target");
            StringAssert.Contains(captured, "0x72756358", "verified XCursor magic");
            StringAssert.Contains(captured, "0x00010000", "verified XCursor version");
            // Activated for the autologin user: transparent theme INSTALLED but
            // console stays on breeze_cursors; lamco toggles per RDP session
            // (connect = transparent for clean stream, disconnect = restore).
            StringAssert.Contains(captured, "cursorTheme breeze_cursors", "console stays on visible theme");
            StringAssert.DoesNotMatch(captured, new System.Text.RegularExpressions.Regex("XCURSOR_THEME\\s*=\\s*transparent"),
                "no forced transparent env (lamco manages live state)");
            StringAssert.Contains(captured, "shakecursorEnabled false", "shakecursor plugin key");
            StringAssert.Contains(captured, "ExecStopPost", "crash-safety cursor restore in unit");
            StringAssert.Contains(captured, "plasma-apply-cursortheme breeze_cursors", "ExecStopPost restores visible theme");
            // The generator shadows EVERY name from installed themes
            // (wallpaper-ghost fix: the desktop uses the "default" role,
            // which is absent from the hardcoded fallback list; XCursor
            // inheritance made the wallpaper show a visible breeze arrow
            // while windows were clean).
            StringAssert.Contains(captured, "breeze_cursors/cursors", "scan source theme dir");
            StringAssert.Contains(captured, "names.update(os.listdir(theme_dir))", "dynamic name shadowing");
            // The names list must include the core cursor roles
            StringAssert.Contains(captured, "left_ptr", "arrow cursor role");
            StringAssert.Contains(captured, "watch", "busy cursor role");
        }

        [TestMethod]
        public async Task ExecuteAsync_RetiresVgem_AndTrustsStockKWin()
        {
            // The capture path is fixed entirely server-side by the lamco fork
            // (DMA-BUF materialize + zero-frame fallback to MemFd): the script
            // must NOT load vgem for a fake renderD128 (DMA-BUF dead end),
            // must clean up vgem artifacts from older deployments, and must
            // NOT force any KWin software-render/SHM env overrides — stock
            // KWin + the fork binary is the shipping path (patches/ removed;
            // KWIN_SCREENCAST_FORCE_SHM was a hand-tuned-VM-only crutch).
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(captured, "modprobe\\s+vgem"),
                "vgem must not be loaded (DMA-BUF path retired)");
            StringAssert.Contains(captured, "rm -f /etc/modules-load.d/vgem.conf", "old vgem artifacts cleaned");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(captured, "KWIN_SCREENCAST_FORCE_SHM"),
                "stock KWin screencast must not be overridden (fork handles buffer types)");
        }

        [TestMethod]
        public async Task ExecuteAsync_ThemeProvision_IsIdempotentSafe_BashShaped()
        {
            // The script must not fail if the theme already exists: it uses
            // rm -rf before cp -r (not a bare cp that errors on existing dirs)
            // and guards the python generator with || warning rather than set -e death.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "rm -rf /usr/share/icons/transparent");
            StringAssert.Contains(captured, "rm -rf /tmp/lamco-transparent-theme");
        }

        [TestMethod]
        public async Task ExecuteAsync_ForkBuild_IncludesKwinVirtualFeatures()
        {
            // The lamco fork must be built with the kwin-virtual strategy
            // (zkde_screencast_unstable_v1 virtual output + libei input) —
            // a fresh VM without these features silently falls back to the
            // old kscreen/DRM scaling path.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "--features x264,vsock,kwin-virtual,libei",
                "fork build must include the kwin-virtual strategy features");
        }

        [TestMethod]
        public async Task ExecuteAsync_ProvisionsIdleLockSuppression()
        {
            // E2E regression guard (TEST_20260901180150): KDE idle autolock
            // wedged the lock greeter under hyperv_drm framebuffer spam and
            // swallowed ALL input including the console — a wedged lock
            // bricks the VM remotely. Provisioning must disable autolock
            // AND install a durable inhibitor holder unit.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            // Config: autolock off at session start (first boot is immune)
            StringAssert.Contains(captured, "--file kscreenlockerrc --group Daemon --key Autolock false",
                "KDE autolock disabled in kscreenlockerrc");
            StringAssert.Contains(captured, "--key LockOnResume false",
                "resume lock disabled too");
            // Durable inhibitor: python holder keeps the D-Bus connection
            // (and thus the cookie) alive for the session lifetime
            StringAssert.Contains(captured, "lamco-idle-inhibit.py", "inhibitor holder script");
            StringAssert.Contains(captured, "ss.Inhibit(", "inhibitor actually requested");
            StringAssert.Contains(captured, "lamco-idle-inhibit.service", "systemd user unit");
            StringAssert.Contains(captured, "WantedBy=graphical-session.target",
                "unit binds to the graphical session");
            StringAssert.Contains(captured, "systemctl --user enable lamco-idle-inhibit.service",
                "unit enabled for future boots");
        }
    }
}