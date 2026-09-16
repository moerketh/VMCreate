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
        private InstallLamcoRdpStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<InstallLamcoRdpStep>> _logger = null!;
        private GalleryItem _supportedItem = null!;
        private GalleryItem _unsupportedItem = null!;
        private VmCustomizations _lamcoCustomizations = null!;
        private VmCustomizations _xrdpCustomizations = null!;
        [TestInitialize]
        public void Setup()
        {
            _step = new InstallLamcoRdpStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            // Runtime distro re-verification: ExecuteAsync reads /etc/os-release
            // via the 2-arg RunCommandAsync before doing anything else. Default
            // to a Debian-family guest (Parrot-shaped: exact ID + ID_LIKE).
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("ID=parrot\nID_LIKE=debian\n");
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
        public async Task ExecuteAsync_ResultLine_Ok_SucceedsQuietly()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("installing...\nLAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Degraded_LogsWarning()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("WARNING: could not determine autologin user\nLAMCO_RESULT=degraded");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("DEGRADED")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Missing_Throws()
        {
            // A zero-exit run with NO LAMCO_RESULT line means the script was
            // swallowed (the exact fabricated-success class already fixed in
            // HyperVVmCreator) — it must throw, not report success.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("some output but no terminal contract line");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        public async Task ExecuteAsync_InvalidInitialUsername_ThrowsBeforeCopy()
        {
            // The username is substituted into a root-run script; the gallery
            // field can be sourced from distro mirror pages. Shell metachar,
            // quotes, paths and whitespace must fail validation on the host.
            var item = new GalleryItem { LinuxDistro = LinuxDistro.Parrot, InitialUsername = "user; rm -rf /" };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, item, _lamcoCustomizations, _logger.Object, CancellationToken.None));

            _shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_BlankInitialUsername_FallsBackToPasswdScan()
        {
            // Blank field does NOT abort the Lamco install (the binary,
            // config, units and TLS perms are user-independent): the script
            // resolves the user from /etc/passwd and flags degraded only if
            // that fails too.
            var item = new GalleryItem { LinuxDistro = LinuxDistro.Parrot, InitialUsername = "" };
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(c => c.Contains("AUTOLOGIN_USER=\"__AUTOLOGIN_USER__\"")),
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
                "blank username leaves the placeholder for the in-script /etc/passwd scan");
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
        public void IsApplicable_TrueForAllDebianFamilyDistros()
        {
            // The install is a pinned amd64 Debian deb — the fork pipeline
            // builds no rpms/flatpaks for this lineage.
            foreach (var distro in new[] { LinuxDistro.Ubuntu, LinuxDistro.Debian, LinuxDistro.Parrot })
            {
                var item = new GalleryItem { LinuxDistro = distro };
                Assert.IsTrue(_step.IsApplicable(item, _lamcoCustomizations),
                    $"{distro} should be supported");
            }
        }

        [TestMethod]
        public void IsApplicable_FalseForRpmDistros_UntilForkShipsRpms()
        {
            // The upstream selection that carried rpm distros was removed with
            // the pinned-deb-only install (rpm/flatpak assets are not built
            // for the fork lineage). These must be gated off in the UI too,
            // not just fail inside the script.
            foreach (var distro in new[] { LinuxDistro.Fedora, LinuxDistro.OpenSuse, LinuxDistro.Unknown })
            {
                var item = new GalleryItem { LinuxDistro = distro };
                Assert.IsFalse(_step.IsApplicable(item, _lamcoCustomizations),
                    $"{distro} must not be offered the Lamco backend (no pinned package exists)");
            }
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysAndRunsScript()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("lamco-rdp-server") &&
                    content.Contains("/etc/os-release") &&
                    content.Contains("config.toml") &&
                    content.Contains("lamco-rdp-server.service")),
                It.Is<string>(p => p.StartsWith("/tmp/install_lamco_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo chown root:root /tmp/install_lamco_")
                                     && cmd.Contains("sudo chmod 0700 /tmp/install_lamco_")
                                     && cmd.Contains("sudo bash /tmp/install_lamco_")
                                     && cmd.Contains("sudo rm -f /tmp/install_lamco_")),
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
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd =>
                    cmd.Contains("sudo chown root:root /tmp/install_lamco_")
                    && cmd.Contains("sudo bash /tmp/install_lamco_")
                    // && (not ;) between every link so a script failure (exit
                    // non-zero) aborts the chain and surfaces as a deployment
                    // failure instead of being masked by the later rm.
                    && cmd.Contains("&& sudo bash /tmp/install_lamco_")
                    && cmd.Contains("&& sudo rm -f /tmp/install_lamco_")
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
        public async Task ExecuteAsync_NoTransparentCursorTheme_ForkOwnsPointerHandling()
        {
            // The fork (>= v1.4.5-hyperv.2) deleted cursor_theme.rs: pointer
            // handling is entirely the transparent color-pointer shape PDU +
            // Runtime Painted auto-selection (config.mode = metadata qualifies
            // — verified against the fork's observe_metadata_cursors). The
            // guest-side transparent XCursor theme, kcminputrc toggles, and
            // ExecStopPost restore targeted that deleted mechanism and must
            // NOT be provisioned anymore.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            Assert.IsFalse(captured.Contains("/usr/share/icons/transparent\n") && captured.Contains("cp -r"),
                "no transparent theme generation/install");
            Assert.IsFalse(captured.Contains("cursorTheme breeze_cursors"),
                "no kcminputrc cursor theme preset");
            Assert.IsFalse(captured.Contains("plasma-apply-cursortheme"),
                "no live cursor theme application");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(captured, @"^ExecStopPost=", System.Text.RegularExpressions.RegexOptions.Multiline),
                "no ExecStopPost cursor restore directive in the unit (the mechanism is deleted)");
            Assert.IsFalse(captured.Contains("shakecursorEnabled"),
                "no shakecursor toggling (tied to the retired transparent theme)");
            // The config must keep cursor.mode = "metadata": it is the
            // precondition for the fork's Runtime Painted auto-flip on
            // metadata-less capture paths (kwin-virtual).
            StringAssert.Contains(captured, "mode = \"metadata\"",
                "cursor mode stays metadata so the fork's Painted auto-flip can engage");
        }

        [TestMethod]
        public async Task ExecuteAsync_CleansUpRetiredTransparentThemeArtifacts()
        {
            // VMs deployed before the retirement carry /usr/share/icons/
            // transparent from the old provisioning; re-running the install
            // must remove it (it can only cause confusion now).
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "rm -rf /usr/share/icons/transparent",
                "retired theme artifacts are cleaned up");
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
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(captured, "modprobe\\s+vgem"),
                "vgem must not be loaded (DMA-BUF path retired)");
            StringAssert.Contains(captured, "rm -f /etc/modules-load.d/vgem.conf", "old vgem artifacts cleaned");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(captured, "KWIN_SCREENCAST_FORCE_SHM"),
                "stock KWin screencast must not be overridden (fork handles buffer types)");
        }

        [TestMethod]
        public async Task ExecuteAsync_RetiredThemeCleanup_IsIdempotentSafe()
        {
            // The retired-theme cleanup must not fail when the directory is
            // absent: rm -rf guarded by [ -d ], not a bare cp/rm sequence.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "if [ -d /usr/share/icons/transparent ]; then",
                "cleanup is guarded (absent dir must not fail the install)");
        }

        [TestMethod]
        public async Task ExecuteAsync_InstallsPinnedForkDeb_OnlyPath()
        {
            // The install path is EXACTLY ONE: the pinned fork deb, verified
            // by sha256 before dpkg. There is no source-build fallback and no
            // upstream fallback — a missing/re-pinned asset must fail the
            // deployment loudly, never silently ship a stock binary.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "LAMCO_FORK_TAG=\"v1.4.5-hyperv.5\"",
                "fork deb tag pinned in the script");
            Assert.IsTrue(captured.Contains("LAMCO_FORK_DEB_SHA256=\"") && !captured.Contains("LAMCO_FORK_DEB_SHA256=\"PENDING"),
                "fork deb sha256 pinned — whoever can push a release asset must not get root on every VM");
            StringAssert.Contains(captured, "sha256sum \"$FORK_DEB_TMP\"",
                "digest verified before dpkg -i");
            // Fork identity comes from the dpkg database: the -hyperv5 marker
            // lives in the deb's Package Version field, NOT in the binary.
            // Fork policy pins Cargo.toml at the upstream base version, so the
            // binary's --version prints bare 1.4.5 forever (verified on the
            // pinned asset). A binary-grep for the marker can never pass.
            StringAssert.Contains(captured, "dpkg-query -W -f='${Version}' lamco-rdp-server",
                "fork identity is read from the dpkg database");
            StringAssert.Contains(captured, "[ \"$installed_pkg_ver\" != \"$LAMCO_FORK_DEB_VERSION\" ]",
                "installed package version must equal the pinned fork version exactly");
            StringAssert.Contains(captured, "[ \"$installed_status\" != \"install ok installed\" ]",
                "package must be in 'install ok installed' state — a half-configured or removed package fails");
            StringAssert.Contains(captured, "grep -aq \" $LAMCO_FORK_CRATE_VERSION\"",
                "binary payload sanity gate: --version must report the pinned crate generation");
            // No fallback paths may exist
            Assert.IsFalse(captured.Contains("cargo build"), "no on-VM source build");
            Assert.IsFalse(captured.Contains("rustup"), "no curl|sh toolchain");
            Assert.IsFalse(captured.Contains("git clone"), "no unpinned fork clone");
            Assert.IsFalse(captured.Contains("releases/latest"), "no latest-tag resolution: the tag is pinned");
            Assert.IsFalse(captured.Contains("DEB_INSTALLED"), "no deb/source branching");
            Assert.IsFalse(captured.Contains("lamco-admin"), "no upstream repo anywhere");
        }

        [TestMethod]
        public async Task ExecuteAsync_DebFailsLoudly_NoSilentDegradation()
        {
            // Guard: the script must exit 1 with a clear message on every
            // failure mode of the only install path — download, digest,
            // dpkg, and the fork-marker check. The era of "WARNING: ...
            // keeping the release binary" silent degradation is over.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "ERROR: download failed for",
                "download failure exits 1");
            StringAssert.Contains(captured, "sha256 mismatch",
                "digest mismatch exits 1");
            StringAssert.Contains(captured, "ERROR: apt-get dependency resolution failed after dpkg -i (dpkg rc=$dpkg_rc)",
                "dependency-fixup failure exits 1 with the dpkg rc preserved for attribution");
            StringAssert.Contains(captured, "does not report the fork marker",
                "dpkg fork-marker check failure exits 1");
            StringAssert.Contains(captured, "does not run or does not",
                "binary payload sanity check failure exits 1");
            Assert.IsFalse(captured.Contains("keeping the release binary"),
                "no silent keep-stock-binary fallback");
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
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

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
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

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
        public async Task ExecuteAsync_ScopesFramebufferSpamToKwinUnitLogFilter()
        {
            // hyperv_drm framebuffer error spam originates from kwin's own
            // stderr under plasma-kwin_wayland.service and exhausts journald's
            // PER-SERVICE rate-limit bucket, drowning kwin's diagnostics and
            // the lamco session lines that share it. Provisioning must scope
            // the fix to that unit (LogFilterPatterns drop-in) instead of
            // raising the global journald burst, which lets the spam through
            // and keeps the journal 90% noise.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "/etc/systemd/user/plasma-kwin_wayland.service.d/lamco-logfilter.conf",
                "scoped log-filter drop-in installed on the kwin user unit");
            StringAssert.Contains(captured, "LogFilterPatterns=~Failed to create framebuffer",
                "framebuffer spam lines are dropped client-side before reaching the journal");
            Assert.IsFalse(captured.Contains("RateLimitBurst=100000"),
                "the global journald burst raise is gone — it flooded the journal at full spam rate and left defaults unsafe for every other service");
            Assert.IsFalse(captured.Contains("systemctl restart systemd-journald"),
                "journald is not restarted anymore — the filter is applied via user-manager daemon-reload");
            StringAssert.Contains(captured, "rm -f /etc/systemd/journald.conf.d/99-lamco-ratelimit.conf",
                "stale global override from older provisioning runs is removed");
            StringAssert.Contains(captured, "python3-dbus",
                "python3-dbus present for the idle-inhibit holder script");
        }

        [TestMethod]
        public async Task ExecuteAsync_ProvisionsVsockTransport()
        {
            // The vsock transport serves Hyper-V Enhanced Session: vmms
            // relays from VMADDR_CID_HOST (CID 2). The fork binds
            // VMADDR_CID_ANY with no accept-time CID allowlist — there is
            // no allowed_cids key in the fork's config schema — so access
            // control is architectural: only Hyper-V's host-side relay
            // can attach to the guest's vsock device. The test pins that
            // the (unsupported) allowlist key is NOT deployed: writing it
            // would be a silent no-op that reads as enforcement.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "[server.transports.vsock]",
                "vsock transport section present in the provisioned config");
            Assert.IsFalse(captured.Contains("allowed_cids ="),
                "the fork has no allowed_cids key — deploying it would be a silent no-op dressed as enforcement");
        }

        [TestMethod]
        public async Task ExecuteAsync_TransportSecurity_PinsLoopbackTcpAndNoAuthExceptions()
        {
            // RDP posture (review decision): TCP loopback only, vsock for
            // Enhanced Session, no firewall rule. auth_method=none over a
            // 0.0.0.0 bind was an unlocked sudo-capable desktop for
            // anything on the Default Switch — and LAN-wide the moment the
            // VM was re-attached to an external switch. These assertions
            // pin the posture so it cannot drift silently again.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "listen_addr = \"127.0.0.1:3389\"",
                "TCP transport binds loopback (auth_method=none must never face a network)");
            Assert.IsFalse(captured.Contains("0.0.0.0:3389"),
                "no wildcard TCP bind anywhere in the config");
            StringAssert.Contains(captured, "auth_method = \"none\"",
                "auth pin: auth stays explicitly none — if this changes, revisit the loopback bind");
            Assert.IsFalse(captured.Contains("ufw allow 3389"),
                "no ufw rule opening 3389 (loopback needs none)");
            Assert.IsFalse(captured.Contains("firewall-cmd --add-port"),
                "no firewalld rule opening 3389");
            StringAssert.Contains(captured, "THREAT MODEL",
                "the config template carries the threat-model comment for future editors");
        }

        [TestMethod]
        public async Task ExecuteAsync_DistroGate_IsDebianFamilyOnly()
        {
            // The pinned fork deb is amd64 Debian packaging; rpm/flatpak
            // assets are not built for this lineage. A non-Debian distro must
            // be rejected at the top of the script, not after a download.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "is not Debian-family",
                "non-Debian distros are refused with exit 1");
            Assert.IsFalse(captured.Contains("dnf"), "no rpm package manager paths");
            Assert.IsFalse(captured.Contains("zypper"), "no rpm package manager paths");
            Assert.IsFalse(captured.Contains("flatpak install"), "no flatpak fallback");
        }

        [TestMethod]
        public async Task ExecuteAsync_DebInstallsBeforeConfigWrite()
        {
            // dpkg -i --force-confnew replaces conffiles with package defaults;
            // if the fork deb ships /etc/lamco-rdp-server/config.toml as a
            // conffile, writing our tuned config BEFORE the install would be
            // clobbered. The config write must come after the deb install.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            var debIdx = captured.IndexOf("dpkg -i --force-confnew", StringComparison.Ordinal);
            var configIdx = captured.IndexOf("cat > /etc/lamco-rdp-server/config.toml", StringComparison.Ordinal);
            Assert.IsTrue(debIdx >= 0, "deb install present");
            Assert.IsTrue(configIdx >= 0, "config write present");
            Assert.IsTrue(debIdx < configIdx,
                "deb install must precede the config.toml write (--force-confnew would clobber it)");
        }

        [TestMethod]
        public async Task ExecuteAsync_ReadinessGateOnRealPath()
        {
            // The readiness gate distinguishes "service up and listening"
            // from "deployed but blocked on the one-time consent dialog".
            // It used to be nested inside the deleted source-build branch
            // (never on the real install path); it must run on the deb path.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "Accept dispatcher started",
                "readiness gate present in the script");
            // The gate is on the main path: it must no longer be nested
            // inside a `command -v cargo` block (the source build is gone).
            Assert.IsFalse(captured.Contains("command -v cargo"),
                "readiness gate is no longer gated on cargo availability");
        }

        [TestMethod]
        public async Task ExecuteAsync_ReadinessGate_IsSessionAware()
        {
            // E2E TEST_20260910224913 (and every fresh deployment): step 235
            // runs BEFORE EnableGraphicalAutologinStep (238) configures
            // autologin, so the VM sits at the DM greeter with NO graphical
            // session. The lamco user units are WantedBy=graphical-session.target
            // — neither the dispatcher nor the consent prompt can have started,
            // and the unconditional 120 s journal poll was a STRUCTURAL
            // timeout-degraded on every fresh deploy (all four TEST_202609101*
            // runs logged LAMCO_RESULT=degraded for exactly this reason).
            // The gate must first check graphical-session.target: inactive →
            // "deferred" (INFO, no DEGRADED — the install is complete, the
            // service starts with the session); only a LIVE session with a
            // silent service may degrade.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            // The session-state check gates the journal poll.
            StringAssert.Contains(captured, "systemctl --user is-active graphical-session.target",
                "the gate checks the session state before polling the journal");
            int sessionCheck = captured.IndexOf("systemctl --user is-active graphical-session.target", StringComparison.Ordinal);
            int dispatcherPoll = captured.IndexOf("Accept dispatcher started", StringComparison.Ordinal);
            Assert.IsTrue(sessionCheck >= 0 && dispatcherPoll > sessionCheck,
                "the session-state check must precede the dispatcher journal poll");
            // The deferred branch exists (assigned in the no-session else arm)
            // and reports an explanatory NOTICE, not a failure.
            StringAssert.Contains(captured, "RDY_OUTCOME=\"deferred\"",
                "a deferred outcome exists for the no-session path");
            StringAssert.Contains(captured, "lamco starts with the session",
                "deferred outcome carries an explanatory NOTICE line");
            // DEGRADED may only appear for the live-session case arms
            // (consent/timeout), never in the deferred arm.
            int deferredArm = captured.IndexOf("deferred)", StringComparison.Ordinal);
            Assert.IsTrue(deferredArm > 0, "the case statement has a deferred arm");
            int deferredArmEnd = captured.IndexOf(";;", deferredArm, StringComparison.Ordinal);
            Assert.IsTrue(deferredArmEnd > deferredArm, "deferred arm is terminated");
            string deferredArmBody = captured.Substring(deferredArm, deferredArmEnd - deferredArm);
            Assert.IsFalse(deferredArmBody.Contains("DEGRADED"),
                "the deferred (no-session) outcome must not set DEGRADED");
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
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

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

        [TestMethod]
        public async Task ExecuteAsync_RetiresPreinstalledXrdp_LamcoOwnsVsock3389()
        {
            // Kali's Hyper-V images bake xrdp in (hyperv-daemons xrdp
            // pipewire-module-xrdp from the image build) listening on
            // vsock://-1:3389 — every vmconnect Enhanced Session landed on
            // xrdp and never on lamco (TEST_20260910165003: the RDP login
            // wedged in xrdp's Xorg session, plasma-ksmserver failed,
            // graphical-session.target dead — the "no KDE login" black
            // screen). The install must stop + disable xrdp/xrdp-sesman
            // and kill sesexec stragglers so lamco's vsock listener binds.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            // TEST_20260910195849: the first version gated on a
            // `systemctl list-unit-files | awk | grep -qx` pipeline that
            // silently no-oped during a real deployment (zero block output,
            // xrdp survived active+enabled, Enhanced Session landed on
            // Xorg). Existence must be a deterministic file check.
            StringAssert.Contains(captured, "[ -f \"/lib/systemd/system/${unit}.service\" ]",
                "unit existence is a deterministic [ -f ] file check, not a systemctl|awk|grep pipeline that can silently no-op");
            StringAssert.Contains(captured, "systemctl stop \"${unit}.service\"",
                "preinstalled xrdp units are stopped");
            StringAssert.Contains(captured, "systemctl disable \"${unit}.service\"",
                "preinstalled xrdp units are disabled so they never race lamco at boot");
            StringAssert.Contains(captured, "pkill -x xrdp-sesexec",
                "straggler sesexec processes (spawned per session by sesman, not a unit) are killed");
            // Belt-and-braces keyed on the process name (single command, no
            // pipeline), not the port (lamco itself legitimately holds
            // 127.0.0.1:3389 on re-runs).
            StringAssert.Contains(captured, "pgrep -x xrdp",
                "post-stop check is a single pgrep — no multi-stage pipeline");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(captured, @"apt-get\s+purge.*xrdp", System.Text.RegularExpressions.RegexOptions.Multiline),
                "xrdp packages are NOT purged — a later Xrdp-backend deployment needs them re-enablable");
        }

        [TestMethod]
        public async Task ExecuteAsync_SentinelSurvivesHostSideSubstitution()
        {
            // TEST_20260910195849: the host substitutes via naive
            // string.Replace of __AUTOLOGIN_USER__ across the WHOLE script,
            // which rewrote the sentinel comparison itself ("x =
            // placeholder" became "kali = kali", always true) and forced a
            // correctly-substituted deployment down the guest-scan path.
            // The sentinel must be BUILT from fragments the Replace cannot
            // match; after substitution the script must still contain a
            // comparison against a runtime-constructed placeholder.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            var item = new GalleryItem { LinuxDistro = LinuxDistro.Kali, InitialUsername = "kali" };
            await _step.ExecuteAsync(_shell.Object, item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            // After substitution the assignment reads AUTOLOGIN_USER="kali"
            // — and the sentinel comparison must NOT have been rewritten to
            // compare against "kali" (the self-clobber); it compares against
            // the runtime-built $_PLACEHOLDER.
            StringAssert.Contains(captured, "AUTOLOGIN_USER=\"kali\"",
                "the gallery username was substituted into the assignment");
            StringAssert.Contains(captured, "[ \"$AUTOLOGIN_USER\" = \"$_PLACEHOLDER\" ]",
                "sentinel comparison references the runtime-built placeholder");
            Assert.IsFalse(captured.Contains("'kali''_USER__'"),
                "the placeholder-construction fragments must not themselves be substituted");
        }

        [TestMethod]
        public async Task ExecuteAsync_UnresolvedUser_HardFails_NotDegradedSuccess()
        {
            // A guest where no autologin user resolves (blank gallery field +
            // empty /etc/passwd scan) means NO user units, NO autologin, NO
            // portal grant — lamco is 100% dead. Reporting that as
            // "completed with warnings" is exactly the TEST_20260910165003
            // fabricated-success class: the deploy "succeeded" and the
            // units lived under /.config at filesystem root. The script must
            // hard-fail (exit 1) on that path.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            // The no-user branch exits 1 (hard failure) BEFORE printing any
            // LAMCO_RESULT line — not the old "LAMCO_RESULT=degraded" +
            // exit 0 which read as deployable in the host log.
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(
                    captured,
                    @"if \[ -z ""\$AUTOLOGIN_USER"" \]; then[\s\S]*?exit 1",
                    System.Text.RegularExpressions.RegexOptions.Multiline),
                "the no-user path exits non-zero (hard failure)");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(
                    captured,
                    @"no autologin user could be resolved[\s\S]{0,500}LAMCO_RESULT=degraded",
                    System.Text.RegularExpressions.RegexOptions.Multiline),
                "the no-user path must not report a machine-readable degraded result — the deployment is dead, not degraded (bounded window: the terminal DEGRADED block 400 lines below is legitimate)");
        }

        [TestMethod]
        public async Task ExecuteAsync_DegradedReasons_ReachTheHostLog()
        {
            // The SSH transport returns STDOUT ONLY on zero-exit runs
            // (stderr is dropped to a debug log). DEGRADED reason lines on
            // stderr were therefore invisible in the deployment log's
            // DEGRADED warning — the host warned without a reason
            // (TEST_20260910165003: "LAMCO_RESULT=degraded" with no
            // readiness-gate detail). Every DEGRADED/WARNING/NOTICE line
            // the host is meant to log must print to stdout.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(
                    captured,
                    @"echo ""(DEGRADED|NOTICE)[^""]*""\s*>&2",
                    System.Text.RegularExpressions.RegexOptions.Multiline),
                "DEGRADED/NOTICE lines must not go to stderr — they never reach the host log on zero-exit runs");
            // Hard-failure ERROR lines MAY use stderr: on non-zero exits the
            // SSH transport captures it into the thrown exception.
            StringAssert.Contains(captured, "readiness gate timed out (120s)",
                "the timeout reason text is present (host logs it with the DEGRADED warning)");
        }
    }
}
