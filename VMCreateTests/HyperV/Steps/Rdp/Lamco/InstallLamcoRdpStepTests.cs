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
        public async Task ExecuteAsync_InstallsPinnedForkPackage_OnlyPath()
        {
            // The install path is EXACTLY ONE per lineage: the pinned fork
            // deb (dpkg/apt lineage) or the pinned fork pacman package
            // (Arch lineage), each verified by sha256 before install. There
            // is no source-build fallback and no upstream fallback — a
            // missing/re-pinned asset must fail the deployment loudly, never
            // silently ship a stock binary.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            // Pin consistency, not a hardcoded tag: every fork bump
            // invalidated the exact-string assertion (left stale at
            // v1.4.5-hyperv.5 when the script moved to .6), so assert the
            // invariants the script's own constants must satisfy instead —
            // tag pinned and non-empty.
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(
                    captured, "LAMCO_FORK_TAG=\"v[^\"]+\""),
                "fork deb tag pinned in the script");
            StringAssert.Contains(captured, "LAMCO_FORK_TAG=\"v1.4.5-hyperv.7\"",
                "current fork lineage at v1.4.5-hyperv.7 (clipboard reliability round)");
            Assert.IsTrue(captured.Contains("LAMCO_FORK_DEB_SHA256=\"") && !captured.Contains("LAMCO_FORK_DEB_SHA256=\"PENDING"),
                "fork deb sha256 pinned — whoever can push a release asset must not get root on every VM");
            StringAssert.Contains(captured, "sha256sum \"$FORK_DEB_TMP\"",
                "digest verified before dpkg -i");
            // Fork identity comes from the dpkg database: the -hyperv7 marker
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
            // Arch/pacman lineage: the same fork tag ships an amd64 pacman
            // package; install_lamco.sh branches on is_arch_family and
            // installs the sha256-verified download via pacman -U.
            StringAssert.Contains(captured, "LAMCO_FORK_PKG_VERSION=\"1.4.5.hyperv7-1\"",
                "fork pacman pkgver pinned in the script (dot, not hyphen — Arch pkgver grammar)");
            Assert.IsTrue(captured.Contains("LAMCO_FORK_PKG_SHA256=\"") && !captured.Contains("LAMCO_FORK_PKG_SHA256=\"PENDING"),
                "fork pacman package sha256 pinned");
            StringAssert.Contains(captured, "sha256sum \"$FORK_PKG_TMP\"",
                "pacman package digest verified before pacman -U");
            StringAssert.Contains(captured, "pacman -U --noconfirm \"$FORK_PKG_TMP\"",
                "Arch lineage installs via pacman -U");
            StringAssert.Contains(captured, "pacman -Q lamco-rdp-server",
                "fork identity on the Arch lineage is read from the pacman database");
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
        public async Task ExecuteAsync_PinsStagingFileTransfer_UnderSandboxedUnit()
        {
            // The hardened user unit runs in a user namespace where setuid
            // fusermount3 cannot mount; "auto" picked FUSE and failed on every
            // start. Staging (~/Downloads, in ReadWritePaths) is the working mode.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "file_transfer_mode = \"staging\"");
            StringAssert.Contains(captured, "ProtectSystem=strict", "the sandbox that rules FUSE out stays on");
            StringAssert.Contains(captured, "%h/Downloads", "staging target is writable inside the sandbox");
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
        public async Task ExecuteAsync_DistroGate_IsDebOrPacmanFamilyOnly()
        {
            // The pinned fork artifacts are amd64 Debian and Arch packaging;
            // rpm/flatpak assets are not built for this lineage. A distro on
            // neither lineage must be rejected at the top of the script, not
            // after a download.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "is neither Debian-family nor pacman-family",
                "distros off both lineages are refused with exit 1");
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
            StringAssert.Contains(captured, ReadinessProbe,
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
            int readinessPoll = captured.IndexOf(ReadinessProbe, StringComparison.Ordinal);
            Assert.IsTrue(sessionCheck >= 0 && readinessPoll > sessionCheck,
                "the session-state check must precede the readiness poll");
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
            StringAssert.Contains(all, ReadinessProbe,
                "readiness gate distinguishes service-up from blocked-on-consent");
        }

        [TestMethod]
        public async Task ExecuteAsync_Kde_PreAuthorizesRemoteDesktopPortal_BeforeFirstSessionStart()
        {
            // Parrot (KDE, no preinstalled vsock xrdp) ended every deploy at
            // ESM 6: lamco blocked on the consent dialog and bound nothing.
            // xdg-desktop-portal-kde skips the dialog for an app id listed
            // "yes" in kde-authorized/remote-desktop; lamco's app id is "".
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            const string seed = "SetPermission sbssas kde-authorized true remote-desktop \"\" 1 yes";
            int seedAt = captured.IndexOf(seed, StringComparison.Ordinal);
            Assert.IsTrue(seedAt >= 0, "kde-authorized/remote-desktop is seeded for app id \"\"");
            string guard = captured[..seedAt];
            StringAssert.Contains(guard[guard.LastIndexOf("if [", StringComparison.Ordinal)..],
                "if [ \"$desktop\" = \"KDE\" ]", "the seed is KDE-only");
            int restartAt = captured.IndexOf("systemctl --user restart lamco-rdp-server.service", StringComparison.Ordinal);
            Assert.IsTrue(restartAt > seedAt, "the seed precedes the first lamco session start");
        }

        [TestMethod]
        public async Task ExecuteAsync_LamcoUnit_WaitsForVsockHoldHandOff_BeforeBinding()
        {
            // lamco-vsock-hold releases vsock:3389 only after polling lamco's
            // unit as active(ating); lamco binds ~0.5s after start and lost
            // the race (EADDRINUSE, unit failed, 5s restart) on every boot.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            int unitStart = captured.IndexOf("/.config/systemd/user/lamco-rdp-server.service\" << 'UNIT_EOF'", StringComparison.Ordinal);
            Assert.IsTrue(unitStart >= 0, "lamco user unit heredoc present");
            string unit = captured[unitStart..captured.IndexOf("\nUNIT_EOF", unitStart, StringComparison.Ordinal)];

            int waitAt = unit.IndexOf("\nExecStartPre=", StringComparison.Ordinal);
            Assert.IsTrue(waitAt >= 0, "lamco waits for the port before ExecStart");
            Assert.IsTrue(waitAt < unit.IndexOf("\nExecStart=", StringComparison.Ordinal));
            string wait = unit[(waitAt + 1)..unit.IndexOf('\n', waitAt + 1)];
            StringAssert.StartsWith(wait, "ExecStartPre=+", "unsandboxed: ss needs AF_NETLINK, which the unit denies");
            StringAssert.Contains(wait, "ss -Hl --vsock");
            StringAssert.Contains(wait, ":3389 ");
            StringAssert.Contains(wait, "$$(seq", "systemd expands a single $ — the shell must receive $(seq)");
            StringAssert.EndsWith(wait.TrimEnd(), "exit 0'", "bounded wait that never fails the unit");
        }

        // The server's own loopback TCP listener: the early vsock hold binds
        // vsock only, so this cannot be satisfied by the hold.
        private const string ReadinessProbe = "ss -H -ltnp 'sport = :3389' 2>/dev/null | grep -a 'lamco'";

        [TestMethod]
        public async Task ExecuteAsync_ConsentGrant_SucceedsOnExitStatusNotTokenFile()
        {
            // The fork stores the restore token in the Secret Service and
            // writes no restore_token file, so a file check never matched:
            // the marker was never written and the grant re-prompted on
            // every boot (live: Parrot 7.4). Success is the exit status.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            string execStart = captured.Split('\n')
                .Single(l => l.StartsWith("ExecStart=", StringComparison.Ordinal) && l.Contains("--grant-permission"));
            StringAssert.Contains(execStart,
                "if timeout 300 /usr/bin/lamco-rdp-server --grant-permission; then touch \"%h/.local/share/lamco-rdp-server/consent-granted\"",
                "the marker is written when the grant exits 0");
            Assert.IsFalse(execStart.Contains("restore_token"),
                "the grant's success must not depend on a token file the fork never writes");
        }

        [TestMethod]
        public async Task ExecuteAsync_ProvisionsPersistentKeyring_OnlyWhenNoneExists()
        {
            // An autologin user has no login keyring, so gnome-keyring keeps
            // the restore token in its in-memory session collection and the
            // consent dialog returns after every reboot (live: Parrot 7.4).
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            int guard = captured.IndexOf("if [ ! -e \"$KR_DIR/default\" ] && [ ! -e \"$KR_DIR/login.keyring\" ]; then", StringComparison.Ordinal);
            int write = captured.IndexOf("> \"$KR_DIR/login.keyring\"", StringComparison.Ordinal);
            Assert.IsTrue(guard > 0 && write > guard,
                "the keyring is only created when the user has no default or login keyring");
            StringAssert.Contains(captured, "printf 'login' > \"$KR_DIR/default\"",
                "the new keyring becomes the default collection");
            // A running daemon only picks up the alias file, not the new
            // collection: it must be restarted to load the keyring.
            int daemonRestart = captured.IndexOf("systemctl --user restart gnome-keyring-daemon.service", StringComparison.Ordinal);
            Assert.IsTrue(daemonRestart > write,
                "the user's keyring daemon is restarted after the keyring is written");
            int restart = captured.IndexOf("systemctl --user restart lamco-rdp-server.service", StringComparison.Ordinal);
            Assert.IsTrue(restart > daemonRestart,
                "the keyring is live before the server restart can surface the consent dialog");
        }

        [TestMethod]
        public async Task ExecuteAsync_GnomeFixes_ManagerStartOwnershipSecurityModeMonitorsVersion()
        {
            // Live (Kali GNOME, VM at the greeter): enable-linger starts
            // user@UID asynchronously and every `systemctl --user` call raced
            // it; root `mkdir -p` left ~/.config/systemd/user root-owned;
            // Mutter Direct builds no vsock plain server; mutter 50
            // segfaults on a version-less monitors.xml.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            // Every enable-linger is followed by a synchronous manager start
            // before the next `systemctl --user` call.
            int from = 0, lingers = 0;
            while ((from = captured.IndexOf("loginctl enable-linger \"$AUTOLOGIN_USER\"", from, StringComparison.Ordinal)) >= 0)
            {
                int start = captured.IndexOf("systemctl start \"user@$(id -u \"$AUTOLOGIN_USER\").service\"", from, StringComparison.Ordinal);
                int userCall = captured.IndexOf("systemctl --user", from, StringComparison.Ordinal);
                Assert.IsTrue(start > from && start < userCall,
                    $"enable-linger at {from} must start user@UID synchronously before any systemctl --user call");
                from++; lingers++;
            }
            Assert.IsTrue(lingers >= 2, "both linger sites are covered");

            int mkdirUnits = captured.IndexOf("mkdir -p \"$USER_HOME/.config/systemd/user\"", StringComparison.Ordinal);
            int handOver = captured.IndexOf("for d in .config .config/systemd .config/systemd/user .local .local/share; do", StringComparison.Ordinal);
            int enable = captured.IndexOf("systemctl --user enable lamco-rdp-server.service", StringComparison.Ordinal);
            Assert.IsTrue(mkdirUnits > 0 && handOver > mkdirUnits && enable > handOver,
                "root-created unit-dir parents are handed to the user before the units are enabled");

            int config = captured.IndexOf("security_mode = \"hybrid\"", StringComparison.Ordinal);
            int gnomeSwitch = captured.IndexOf("[ -f /usr/share/wayland-sessions/gnome.desktop ] && [ ! -f /usr/share/wayland-sessions/plasma.desktop ]", StringComparison.Ordinal);
            Assert.IsTrue(config > 0 && gnomeSwitch > config,
                "a Plasma-less GNOME guest is switched to the single plain server after the config write");
            StringAssert.Contains(captured, "s/^security_mode = \"hybrid\"$/security_mode = \"rdp\"/");

            StringAssert.Contains(captured, "<monitors version=\"2\">");
            Assert.IsFalse(captured.Contains("<monitors>\n"), "no version-less monitors.xml is written");

            // Omarchy's update-guard hook aborts any direct -Syu (live: 4.0.6).
            StringAssert.Contains(captured, "OMARCHY_ALLOW_DIRECT_PACMAN=1 pacman -Syu --noconfirm");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(captured, @"(?m)^\s*pacman -Syu"),
                "every -Syu carries the Omarchy guard bypass");
        }

        [TestMethod]
        public async Task ExecuteAsync_ReadinessGate_ConsentIsNoticeNotDegraded()
        {
            // StartGraphicalSessionStep owns the session verdict and reports
            // consent ok-with-notice; the install gate must not degrade the
            // install for the same expected one-click state.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            int arm = captured.IndexOf("        consent)", StringComparison.Ordinal);
            Assert.IsTrue(arm > 0, "the case statement has a consent arm");
            int armEnd = captured.IndexOf(";;", arm, StringComparison.Ordinal);
            string body = captured.Substring(arm, armEnd - arm);
            Assert.IsFalse(body.Contains("DEGRADED=1"), "consent pending must not degrade the install");
            Assert.IsFalse(captured.Contains("| grep -aqi \"permission dialog will appear\""),
                "journalctl | grep -q returns 141 under pipefail even on a match");
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
        public async Task ExecuteAsync_SubstitutesConfiguredConsoleResolution()
        {
            // The console resolution from the Settings window (DefaultConsoleResolution
            // → VmCustomizations) is substituted host-side into every compositor
            // config the script writes: Hyprland Lua mode, GNOME monitors.xml
            // WxH@R triple, and the kscreen-doctor autostart command.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            var customizations = new VmCustomizations { RdpBackend = RdpBackend.Lamco, ConsoleResolution = "1600x900@60" };
            await _step.ExecuteAsync(_shell.Object, _supportedItem, customizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "mode = \"1600x900@60\"",
                "Hyprland Lua rule carries the configured mode");
            StringAssert.Contains(captured, "<width>1600</width>",
                "GNOME monitors.xml width");
            StringAssert.Contains(captured, "<height>900</height>",
                "GNOME monitors.xml height");
            StringAssert.Contains(captured, "<rate>60.000</rate>",
                "GNOME monitors.xml rate uses the three-decimal form");
            StringAssert.Contains(captured, "kscreen-doctor output.1.mode.1600x900@60",
                "KDE autostart forces the configured mode");
            Assert.IsFalse(captured.Contains("__CONSOLE_"),
                "every console token must be substituted before the script reaches the guest");
        }

        [TestMethod]
        public async Task ExecuteAsync_InvalidConsoleResolution_ThrowsBeforeDistroVerification()
        {
            // The resolution comes from a hand-editable settings file; a
            // hostile value must fail host-side shape validation before the
            // distro re-verification runs or anything is copied to the VM.
            var customizations = new VmCustomizations { RdpBackend = RdpBackend.Lamco, ConsoleResolution = "1600x900@60; rm -rf /" };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _supportedItem, customizations, _logger.Object, CancellationToken.None));

            _shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            // Validation sits before the runtime distro re-verification, so no
            // SSH round-trip should have happened either.
            _shell.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _shell.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
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

        [TestMethod]
        public async Task ExecuteAsync_VsockHoldUnit_DropsConflictsAndFailsLoudly()
        {
            // The early vsock hold unit once carried
            // Conflicts=lamco-rdp-server.service: whenever the lamco user unit
            // was already started (a redeploy into a live session), systemd
            // stopped the exact service this step exists to install. The hold
            // must hand off by polling, never by conflict, and its own
            // bootstrap failures must be loud DEGRADED lines on stdout (the
            // host log only sees stdout on zero-exit SSH runs).
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            // No Conflicts= wiring in the hold unit body (mentions survive only
            // inside # comments, which the line-anchored regex excludes).
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(
                    captured,
                    @"^Conflicts=.*$",
                    System.Text.RegularExpressions.RegexOptions.Multiline),
                "the hold unit must not carry Conflicts= against the lamco user unit -- it hands off by polling");
            // Hand-off mechanism: the holder polls the lamco user unit over the
            // machine scope and exits as soon as it is active or activating.
            StringAssert.Contains(captured, "[\"systemctl\", \"--user\", \"-M\", f\"{USER}@\", \"is-active\",",
                "the holder polls the lamco user unit to time its hand-off");
            StringAssert.Contains(captured,
                "systemctl daemon-reload 2>/dev/null || { echo \"DEGRADED: systemctl daemon-reload failed — lamco-vsock-hold.service not registered\"; DEGRADED=1; }",
                "a daemon-reload failure for the hold unit is reported as DEGRADED");
            StringAssert.Contains(captured,
                "systemctl enable lamco-vsock-hold.service >/dev/null 2>&1 || { echo \"DEGRADED: systemctl enable lamco-vsock-hold.service failed — no early listener for the next boot's probe\"; DEGRADED=1; }",
                "an enable failure for the hold unit is reported as DEGRADED");
            StringAssert.Contains(captured,
                "systemctl start lamco-vsock-hold.service 2>/dev/null || { echo \"DEGRADED: systemctl start lamco-vsock-hold.service failed — no listener for this boot's probe\"; DEGRADED=1; }",
                "a start failure for the hold unit is reported as DEGRADED");
            Assert.IsFalse(captured.Contains("systemctl enable lamco-vsock-hold.service >/dev/null 2>&1 || true"),
                "the hold-unit enable must not be a swallowed failure");
        }

        [TestMethod]
        public async Task ExecuteAsync_GrantPermissionTls_SymlinksUserConfigDirAfterChown()
        {
            // lamco-grant.service runs --grant-permission, which loads TLS
            // material from the hardcoded ~/.config/lamco-rdp-server cert/key
            // paths and hard-fails on ENOENT before the portal consent dialog
            // can render -- leaving no restore token behind. The /etc certs
            // must be symlinked into the user config dir AFTER the recursive
            // chown: a chown running after the links would follow them and hand
            // root's key.pem to the autologin user.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            var mkdirIdx = captured.IndexOf("mkdir -p \"$USER_HOME/.config/lamco-rdp-server", StringComparison.Ordinal);
            var chownIdx = captured.IndexOf("chown -R \"$AUTOLOGIN_USER\" \"$USER_HOME/.config/lamco-rdp-server\"", StringComparison.Ordinal);
            var certIdx = captured.IndexOf("ln -sf /etc/lamco-rdp-server/cert.pem \"$USER_HOME/.config/lamco-rdp-server/cert.pem\"", StringComparison.Ordinal);
            var keyIdx = captured.IndexOf("ln -sf /etc/lamco-rdp-server/key.pem \"$USER_HOME/.config/lamco-rdp-server/key.pem\"", StringComparison.Ordinal);
            var unitIdx = captured.IndexOf("cat > \"$USER_HOME/.config/systemd/user/lamco-rdp-server.service\" << 'UNIT_EOF'", StringComparison.Ordinal);
            Assert.IsTrue(mkdirIdx >= 0, "the user config dir is created");
            Assert.IsTrue(chownIdx >= 0, "the user config dir is chowned to the autologin user");
            Assert.IsTrue(certIdx >= 0, "the /etc cert is symlinked where --grant-permission reads it");
            Assert.IsTrue(keyIdx >= 0, "the /etc key is symlinked where --grant-permission reads it");
            Assert.IsTrue(unitIdx >= 0, "the RDP user unit is written in the same block");
            Assert.IsTrue(mkdirIdx < chownIdx && chownIdx < certIdx && certIdx < keyIdx && keyIdx < unitIdx,
                "mkdir -> chown -R -> cert/key symlinks -> unit heredoc: the recursive chown must never see the symlinks, and both links must be in place before the unit that needs them is written");
        }

        [TestMethod]
        public async Task ExecuteAsync_IdleInhibitProvision_GatedOffArch()
        {
            // The idle-lock provision (kscreenlockerrc Autolock=false plus a
            // durable inhibitor holder unit) exists to keep the KDE screen
            // locker from wedging the VM behind a lock screen. Under Omarchy's
            // Hyprland it is meaningless: graphical-session.target never
            // activates and there is no KDE locker, so the holder unit only
            // restart-loops (the Omarchy E2E hit NRestarts of thousands). The
            // provision must sit inside the negative is_arch_family gate while
            // the shared grant/RDP bootstrap stays distro-independent.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            var archGateIdx = captured.IndexOf("if ! is_arch_family; then", StringComparison.Ordinal);
            var idleIdx = captured.IndexOf("lamco-idle-inhibit.py", StringComparison.Ordinal);
            Assert.IsTrue(archGateIdx >= 0, "a negative Arch-family gate exists");
            Assert.IsTrue(idleIdx >= 0, "the idle-inhibit holder stays provisioned for KDE-family distros");
            Assert.IsTrue(archGateIdx < idleIdx,
                "the idle-inhibit provision must skip the Arch family -- under Hyprland it can only restart-loop");
            StringAssert.Contains(captured, "NRestarts≈4000",
                "the script documents the restart-loop evidence behind the Arch exclusion");
            StringAssert.Contains(captured,
                "o.exec_on_start(\"systemctl --user start lamco-grant.service lamco-rdp-server.service\")",
                "the session-start hook starts exactly the grant and RDP units");
            Assert.IsFalse(captured.Contains("systemctl --user start lamco-grant.service lamco-rdp-server.service lamco-idle-inhibit.service"),
                "the session-start hook must not force-start the idle unit -- it belongs to the KDE session target, not to every boot");
        }

        [TestMethod]
        public async Task ExecuteAsync_EnableBlockFailures_ReportDegradedNotSwallowed()
        {
            // The linger + user-unit enable chain decides whether lamco ever
            // starts. Every command ended in `|| true`: a broken enable-linger
            // or enable looked like success while nothing would ever start --
            // exactly the deployed-but-silent condition behind the missing
            // pop-up. Each chain link must fall through to a stdout DEGRADED
            // line (the host log never sees stderr on zero-exit runs).
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync("LAMCO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _supportedItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            Assert.IsFalse(captured.Contains("loginctl enable-linger \"$AUTOLOGIN_USER\" 2>/dev/null || true"),
                "enable-linger failures must not be swallowed with `|| true`");
            StringAssert.Contains(captured,
                "loginctl enable-linger \"$AUTOLOGIN_USER\" 2>/dev/null || { echo \"DEGRADED: enable-linger failed for $AUTOLOGIN_USER — user units will not start at boot\"; DEGRADED=1; }",
                "an enable-linger failure reports DEGRADED on stdout");
            StringAssert.Contains(captured,
                "systemctl --user daemon-reload 2>/dev/null || { echo \"DEGRADED: systemctl --user daemon-reload failed for $AUTOLOGIN_USER\"; DEGRADED=1; }",
                "an enable-block daemon-reload failure reports DEGRADED on stdout");
            StringAssert.Contains(captured,
                "systemctl --user enable lamco-rdp-server.service 2>/dev/null || { echo \"DEGRADED: systemctl --user enable lamco-rdp-server.service failed\"; DEGRADED=1; }",
                "an RDP enable failure reports DEGRADED on stdout");
            StringAssert.Contains(captured,
                "systemctl --user enable lamco-grant.service 2>/dev/null || { echo \"DEGRADED: systemctl --user enable lamco-grant.service failed\"; DEGRADED=1; }",
                "a grant enable failure reports DEGRADED on stdout");
            StringAssert.Contains(captured,
                "systemctl --user enable lamco-idle-inhibit.service 2>/dev/null || { echo \"DEGRADED: systemctl --user enable lamco-idle-inhibit.service failed\"; DEGRADED=1; }",
                "an idle-inhibit enable failure (KDE path) reports DEGRADED on stdout");
        }
    }
}
