using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
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
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

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
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_UsesAndNotSemicolon_SoScriptFailurePropagates()
        {
            // Regression guard: the command must use "&&" (not ";") between the script
            // invocation and the cleanup, so a non-zero exit code from the script is
            // returned to SSH and surfaces as a deployment failure instead of being
            // masked by the always-succeeding rm.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo bash /tmp/install_lamco.sh && sudo rm -f /tmp/install_lamco.sh")
                                     && !cmd.Contains(".sh; sudo")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptFailure_ThrowsAndIsNotSwallowed()
        {
            // Regression guard for the TEST VM incident where a bash syntax error in the
            // embedded script was swallowed (GUI reported success, no Lamco installed).
            // The step must let the SSH exception propagate so the orchestrator reports failure.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            // Generated + installed under the system icon path (KDE-gated by kwriteconfig presence)
            StringAssert.Contains(captured, "kwriteconfig6", "KDE detection gate");
            StringAssert.Contains(captured, "/usr/share/icons/transparent", "theme install target");
            StringAssert.Contains(captured, "0x72756358", "verified XCursor magic");
            StringAssert.Contains(captured, "0x00010000", "verified XCursor version");
            // Activated for the autologin user (kcminputrc + environment.d fallback)
            StringAssert.Contains(captured, "cursorTheme transparent", "kcminputrc activation");
            StringAssert.Contains(captured, "90-lamco-cursor.conf", "environment.d fallback file");
            StringAssert.Contains(captured, "XCURSOR_THEME=transparent", "environment variable");
            // Layered persistence (plasma env script) + XCURSOR_SIZE pinning
            StringAssert.Contains(captured, "/etc/xdg/plasma-workspace/env/40-lamco-cursor.sh", "plasma env script");
            StringAssert.Contains(captured, "XCURSOR_SIZE=24", "cursor size pinned");
            // Live-apply uses the TOGGLE trick (breeze_cursors first, then
            // transparent): plasma-apply-cursortheme alone no-ops with
            // "already set" when config already names transparent - it never
            // swaps the live sprite (verified 2026-08-22 on Parrot 7 / Plasma 6.3).
            StringAssert.Contains(captured, "plasma-apply-cursortheme breeze_cursors", "toggle step 1: real theme");
            StringAssert.Contains(captured, "plasma-apply-cursortheme transparent", "toggle step 2: transparent");
            // Shake Cursor effect disabled (grows the sprite on wiggle —
            // leaks the guest cursor into the video even when transparent)
            StringAssert.Contains(captured, "shakecursorEnabled false", "shakecursor plugin key");
            StringAssert.Contains(captured, "unloadEffect shakecursor", "shakecursor runtime unload");
            // The names list must include the core cursor roles
            StringAssert.Contains(captured, "left_ptr", "arrow cursor role");
            StringAssert.Contains(captured, "watch", "busy cursor role");
        }

        [TestMethod]
        public async Task ExecuteAsync_RetiresVgem_AndProvisionsSoftwareRenderEnv()
        {
            // The capture path is all-software (MemFd + llvmpipe on card0): the
            // script must NOT load vgem for a fake renderD128 (DMA-BUF dead
            // end), must clean up vgem artifacts from older deployments, and
            // must install the KWin software-render/SHM env that the patched
            // screencast.so needs (KWIN_SCREENCAST_FORCE_SHM was previously
            // only present on the hand-tuned test VM, never provisioned).
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(captured, "modprobe\\s+vgem"),
                "vgem must not be loaded (DMA-BUF path retired)");
            StringAssert.Contains(captured, "rm -f /etc/modules-load.d/vgem.conf", "old vgem artifacts cleaned");
            StringAssert.Contains(captured, "kwin-software-render.sh", "software render env file");
            StringAssert.Contains(captured, "LIBGL_ALWAYS_SOFTWARE=1", "Mesa software GL");
            StringAssert.Contains(captured, "MESA_LOADER_DRIVER_OVERRIDE=kms_swrast", "kms_swrast loader override");
            StringAssert.Contains(captured, "KWIN_SCREENCAST_FORCE_SHM=1", "force SHM screencast formats");
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
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "rm -rf /usr/share/icons/transparent");
            StringAssert.Contains(captured, "rm -rf /tmp/lamco-transparent-theme");
        }
    }
}