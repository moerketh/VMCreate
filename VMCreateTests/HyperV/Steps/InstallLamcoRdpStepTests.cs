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
            // Guard: a bash syntax error in the embedded script must surface
            // as a deployment failure. The step must let the SSH exception
            // propagate so the orchestrator reports failure.
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
            // (the desktop background uses the "default" role, which is
            // absent from the hardcoded fallback list; XCursor inheritance
            // would make the wallpaper show a visible breeze arrow while
            // windows are clean).
            StringAssert.Contains(captured, "breeze_cursors/cursors", "scan source theme dir");
            StringAssert.Contains(captured, "names.update(os.listdir(theme_dir))", "dynamic name shadowing");
            // The names list must include the core cursor roles
            StringAssert.Contains(captured, "left_ptr", "arrow cursor role");
            StringAssert.Contains(captured, "watch", "busy cursor role");
        }

        [TestMethod]
        public async Task ExecuteAsync_RetiresVgem_AndTrustsStockKWin()
        {
            // The capture path is fixed entirely server-side (DMA-BUF
            // materialize + zero-frame fallback to MemFd): the script must
            // NOT load vgem for a fake renderD128 (DMA-BUF dead end), must
            // clean up vgem artifacts from older deployments, and must
            // NOT force any KWin software-render/SHM env overrides — stock
            // KWin + the server binary is the shipping path.
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
            // The fork must be built with the kwin-virtual strategy
            // (zkde_screencast_unstable_v1 virtual output + libei input) —
            // a build without these features has no virtual-output capture.
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
            // KDE idle autolock can wedge the lock greeter under hyperv_drm
            // framebuffer spam and swallow ALL input including the console —
            // a wedged lock bricks the VM remotely. Provisioning must disable
            // autolock AND install a durable inhibitor holder unit.
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

        [TestMethod]
        public async Task ExecuteAsync_ProvisionsKwinPrivateInterfaceGrant()
        {
            // KWin 6.x only advertises zkde_screencast_unstable_v1 to clients
            // whose .desktop file lists it under X-KDE-Wayland-Interfaces
            // (executable-path match). Without the entry the kwin-virtual
            // strategy cannot bind the global: every connect fails with
            // "zkde stream creation failed: global not bound".
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "/usr/share/applications/lamco-rdp-server.desktop",
                "desktop file installed at the system applications path");
            StringAssert.Contains(captured, "Exec=/usr/bin/lamco-rdp-server",
                "Exec must match the lamco binary path (KWin matches by executable path)");
            StringAssert.Contains(captured, "X-KDE-Wayland-Interfaces=zkde_screencast_unstable_v1",
                "the private interface must be declared for KWin to advertise it");
            StringAssert.Contains(captured, "kbuildsycoca6 --noincremental",
                "service cache refreshed so the grant applies without relogin");
        }

        [TestMethod]
        public async Task ExecuteAsync_ProvisionsJournaldRateLimitRelief()
        {
            // hyperv_drm framebuffer error spam exhausts journald's default
            // rate limit within seconds, after which ALL user-session logs
            // are silently dropped — including the lamco/kwin-virtual lines
            // needed to diagnose live sessions. Provisioning must raise the
            // burst.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "/etc/systemd/journald.conf.d/99-lamco-ratelimit.conf",
                "journald override installed");
            StringAssert.Contains(captured, "RateLimitBurst=100000",
                "rate limit burst raised so session logs survive framebuffer spam");
            StringAssert.Contains(captured, "python3-dbus",
                "python3-dbus present for the idle-inhibit holder script");
        }

        [TestMethod]
        public async Task ExecuteAsync_ProvisionsVsockCidAllowlist()
        {
            // The vsock transport serves Hyper-V Enhanced Session: vmms
            // relays from VMADDR_CID_HOST (CID 2). The listener binds
            // VMADDR_CID_ANY (no bind-time filter), so the accept-time
            // allowlist is the only access control confining it to the
            // host relay.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "[server.transports.vsock]",
                "vsock transport section present in the provisioned config");
            StringAssert.Contains(captured, "allowed_cids = [2]",
                "vsock accept-time CID allowlist confined to the host relay (VMADDR_CID_HOST)");
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysForkV3Branch()
        {
            // The fork line carrying per-transport security routing
            // (dual-server), the vsock CID allowlist, kwin-virtual, and the
            // client-size/silent-adoption fixes is feature/hyperv-
            // enhanced-session-v3. A stale branch pin silently deploys the
            // pre-fix line.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "LAMCO_FORK_BRANCH=\"feature/hyperv-enhanced-session-v3\"",
                "fork deploy tracks the v3 branch (per-transport security + allowlist line)");
            StringAssert.Contains(captured, "--features x264,vsock,kwin-virtual,libei",
                "fallback source build keeps the full deployment feature set");
        }

        [TestMethod]
        public async Task ExecuteAsync_InstallsForkReleaseDebWithSourceFallback()
        {
            // The pipeline-built deb cuts the ~18-minute on-VM Rust build to a
            // dpkg; the source build remains the fallback when the deb is
            // unreachable. Both paths must be visible in the script. The step
            // copies several files; assert over the accumulated content.
            var captured = new List<string>();
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured.Add(content));
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            var all = string.Join("\n", captured);
            StringAssert.Contains(all, "LAMCO_FORK_DEB_URL=",
                "pipeline deb is the preferred install path (URL built from repo/tag)");
            StringAssert.Contains(all, "v1.4.5-hyperv.1",
                "deb URL points at the fork release tag");
            StringAssert.Contains(all, "DEB_INSTALLED=1",
                "deb success flag drives the source-build skip");
            StringAssert.Contains(all, "falling back to on-VM source build",
                "deb failure falls back to the source build");
        }

        [TestMethod]
        public async Task ExecuteAsync_ProvisionsOneShotConsentGrant()
        {
            // Without a stored portal restore token the server's session
            // creation blocks on the RemoteDesktop consent dialog and NO
            // listener binds (a fresh VM looks deployed-but-dead). The
            // oneshot lamco-grant service runs --grant-permission at first
            // graphical-session start so the dialog appears exactly once.
            var captured = new List<string>();
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured.Add(content));
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            var all = string.Join("\n", captured);
            StringAssert.Contains(all, "lamco-grant.service",
                "oneshot grant unit provisioned");
            StringAssert.Contains(all, "--grant-permission",
                "grant flow obtains and stores the restore token");
            StringAssert.Contains(all, "consent-granted",
                "marker file makes the oneshot skip after a successful grant");
            StringAssert.Contains(all, "WantedBy=graphical-session.target",
                "grant runs at graphical session start (dialog on the console)");
            StringAssert.Contains(all, "Accept dispatcher started",
                "readiness gate distinguishes service-up from blocked-on-consent");
        }
    }
}