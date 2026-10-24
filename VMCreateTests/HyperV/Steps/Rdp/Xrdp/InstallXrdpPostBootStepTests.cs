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
    /// Tests for <see cref="InstallXrdpPostBootStep"/> — the Auto-backend
    /// backfill: under <see cref="RdpBackend.Auto"/> the chroot xrdp
    /// install is skipped (no VMCREATE_XRDP KVP), so when the resolver
    /// picks xrdp this step installs it over SSH. It also runs as a
    /// safety net for explicit <see cref="RdpBackend.Xrdp"/> deployments
    /// (one dpkg -s verifies the chroot install).
    /// </summary>
    [TestClass]
    public sealed class InstallXrdpPostBootStepTests
    {
        private InstallXrdpPostBootStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<InstallXrdpPostBootStep>> _logger = null!;
        private GalleryItem _item = null!;
        private VmCustomizations _xrdpCustomizations = null!;
        [TestInitialize]
        public void Setup()
        {
            _step = new InstallXrdpPostBootStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<InstallXrdpPostBootStep>>();
            _item = new GalleryItem { LinuxDistro = LinuxDistro.Kali };
            _xrdpCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Xrdp };
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Install xrdp", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(236, _step.Order);
            Assert.AreEqual("Sub_InstallXrdpPostBoot", _step.ProgressPhaseId);
            Assert.AreEqual("Verify xrdp listener", _step.DeployTitle);
            Assert.IsTrue(_step.SupportsChroot, "the offline install half runs in the chroot");
            Assert.IsTrue(_step.RunsAfterChroot, "the verify half re-runs post-boot");
        }

        [TestMethod]
        public async Task ExecuteAsync_InChroot_PassesChrootFlag_AndTreatsUnverifiedAsExpected()
        {
            var chroot = new Mock<IGuestShell>();
            chroot.Setup(s => s.Context).Returns(GuestExecutionContext.Chroot);
            chroot.Setup(s => s.VmName).Returns("TestVM");
            chroot.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("XRDP_POSTBOOT_RESULT=already\nXRDP_POSTBOOT_TRANSPORT=unverified\nCHROOT: offline half complete");

            await _step.ExecuteAsync(chroot.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);

            chroot.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo bash /tmp/install_xrdp_postboot_") && cmd.Contains(" --chroot")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
                "the chroot half must invoke the script with --chroot");
        }

        [TestMethod]
        public async Task ExecuteAsync_Live_PassesNoChrootFlag()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("XRDP_POSTBOOT_RESULT=already\nXRDP_POSTBOOT_TRANSPORT=vsock\n");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo bash /tmp/install_xrdp_postboot_") && !cmd.Contains(" --chroot")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
                "the live verify half must NOT pass --chroot");
        }

        [TestMethod]
        public void IsApplicable_OnlyForXrdpBackend()
        {
            // Runs for explicit Xrdp AND post-Auto-resolution (the resolver
            // mutates the backend in place before this step's gate is read).
            Assert.IsTrue(_step.IsApplicable(_item, _xrdpCustomizations));

            foreach (var backend in new[] { RdpBackend.Auto, RdpBackend.Lamco, RdpBackend.None })
                Assert.IsFalse(_step.IsApplicable(_item, new VmCustomizations { RdpBackend = backend }),
                    $"IsApplicable must be false for {backend}");

            Assert.IsFalse(_step.IsApplicable(_item, null));
        }

        [TestMethod]
        public void IsApplicable_FalseForNixOsItem_EvenWithExplicitXrdp()
        {
            // Safety net for the user who forces xrdp on a Nix guest: the
            // nix store has no apt/dnf/zypper/pacman install path for the
            // script's package list, so the script would exit 1 and hard-
            // fail an otherwise healthy deployment. The deployment
            // intentionally ends RDP-less instead.
            var nixItem = new GalleryItem { LinuxDistro = LinuxDistro.NixOs };
            Assert.IsFalse(_step.IsApplicable(nixItem, _xrdpCustomizations));

            // ...and a NixOs item cannot resurrect the step via any other
            // backend either (None keeps it off; Auto resolves before this
            // step, to None for Nix).
            foreach (var backend in new[] { RdpBackend.Auto, RdpBackend.Lamco, RdpBackend.None })
                Assert.IsFalse(_step.IsApplicable(nixItem, new VmCustomizations { RdpBackend = backend }),
                    $"IsApplicable must be false for a NixOs item with {backend}");
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Installed_Succeeds()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("Reading package lists...\nXRDP_POSTBOOT_RESULT=installed");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Already_Succeeds()
        {
            // The chroot install (explicit xrdp deployments) leaves xrdp
            // present — the script short-circuits with "already".
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("XRDP_POSTBOOT_RESULT=already");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Missing_Throws()
        {
            // A zero-exit run with NO result line means the script was
            // swallowed — fabricated-success class; it must throw so the
            // deployment reports failure, never a silent green.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("some output but no terminal contract line");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        [DataRow("tcp")]
        [DataRow("unverified")]
        public async Task ExecuteAsync_TransportFallbackOrUnverified_Succeeds(string transport)
        {
            // A TCP fallback (vsock-less distro build, rolled back by the
            // script) and an unverified transport (no systemctl) are loud
            // warnings, not failures — the image still has network RDP.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync($"XRDP_POSTBOOT_RESULT=installed\nXRDP_POSTBOOT_TRANSPORT={transport}");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);
        }

        [TestMethod]
        public async Task ExecuteAsync_ShipsScriptWithUniformGuestHardening()
        {
            // Same guest-script contract as every shipped step: GUID path in
            // /tmp, chown root:root, chmod 0700, bash, rm -f.
            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, path, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("XRDP_POSTBOOT_RESULT=already");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(c => c.Contains("apt-get") || c.Contains("dnf") || c.Contains("pacman")),
                It.Is<string>(p => p.StartsWith("/tmp/install_xrdp_postboot_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);

            Assert.IsNotNull(copiedContent, "script content must reach the guest");
            StringAssert.Contains(copiedContent, "XRDP_POSTBOOT_RESULT=", "script carries the result-line contract");
        }

        [TestMethod]
        public async Task ExecuteAsync_ShipsScriptWithVsockEnhancedSessionConfig()
        {
            // The PwnCloudOS lesson: stock xrdp listens on TCP 3389 only, so
            // vmconnect silently falls back to the basic console and the
            // Enhanced Session popup never appears. The post-boot backfill
            // runs on every path (installed OR already) and MUST append the
            // vsock listener (keeping TCP) plus the security pins — and it
            // must report the transport outcome and roll back on failure,
            // so a vsock-less distro build degrades loudly, not silently.
            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, path, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("XRDP_POSTBOOT_RESULT=already\nXRDP_POSTBOOT_TRANSPORT=vsock");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(copiedContent);
            // The append form carries the URL unescaped; the old sed-replace
            // form carried it as port=vsock:\/\/-1:3389. Accept either so a
            // future rework of the insertion line cannot silently drop the
            // listener config again.
            Assert.IsTrue(
                copiedContent.Contains("port=vsock://-1:3389", StringComparison.Ordinal)
                || copiedContent.Contains("port=vsock:\\/\\/-1:3389", StringComparison.Ordinal),
                "the HvSocket listener config is what makes the Enhanced Session popup appear");
            StringAssert.Contains(copiedContent, "security_layer=rdp", StringComparison.Ordinal);
            StringAssert.Contains(copiedContent, "crypt_level=none", StringComparison.Ordinal);
            StringAssert.Contains(copiedContent, "systemctl restart xrdp",
                "an xrdp whose ini lacks the vsock listener still gets the restart to pick up the config");
            StringAssert.Contains(copiedContent, "XRDP_POSTBOOT_TRANSPORT=",
                "the transport outcome must be machine-readable so the host can warn on the TCP fallback");
            StringAssert.Contains(copiedContent, "vmcreate-bak",
                "a vsock-less distro build must be rolled back instead of left without RDP");
        }

        [TestMethod]
        public async Task ExecuteAsync_Script_ChrootKeepsBackup_FirstBootRestoresTcpFallback()
        {
            // The vsock-only config write means a vsock-less xrdp build has
            // no listener unless the TCP config is recovered. Two contracts
            // pin that: the CHROOT half must LEAVE the .vmcreate-bak on disk
            // (deleting it stranded the fallback), and the first-boot skip
            // gate must restore that backup and restart when the daemon
            // runs without the vsock listener — the same recovery as the
            // live rollback, safe on first boot because no RDP session
            // exists yet to orphan.
            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("XRDP_POSTBOOT_RESULT=already\nXRDP_POSTBOOT_TRANSPORT=vsock");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(copiedContent);

            // 1. The chroot half keeps the backup (no rm between the backup
            //    write and the CHROOT_MODE exit).
            int chrootExit = copiedContent.IndexOf("CHROOT: xrdp installed and configured offline", StringComparison.Ordinal);
            Assert.IsTrue(chrootExit > 0, "the chroot half's exit block must exist");
            string chrootBlock = copiedContent.Substring(0, chrootExit);
            Assert.IsFalse(chrootBlock.Contains("rm -f \"$INI.vmcreate-bak\"",
                StringComparison.Ordinal),
                "the chroot half must keep the pre-edit TCP backup for first boot's fallback");

            // 2. The first-boot skip gate restores the backup when the
            //    daemon is active without a vsock listener (and when it is
            //    not active), before any "TCP RDP kept" claim.
            StringAssert.Contains(copiedContent, "cp \"$INI.vmcreate-bak\" \"$INI\"",
                "the skip gate must restore the TCP backup when vsock never comes up");
            int restoreIdx = copiedContent.IndexOf("cp \"$INI.vmcreate-bak\" \"$INI\"", StringComparison.Ordinal);
            int keptIdx = copiedContent.IndexOf("TCP RDP kept", StringComparison.Ordinal);
            Assert.IsTrue(restoreIdx >= 0 && keptIdx > restoreIdx,
                "the restore must exist in the skip gate before any TCP-kept message");
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptKeysSkipOnVsockConfig_NotPackagePresence()
        {
            // Regression: the skip used to key on dpkg/rpm/pacman presence,
            // so a preinstalled xrdp WITHOUT the vsock listener was reported
            // "tcp" and never fixed, and a chroot run against preinstalled
            // xrdp exited without writing the config at all. The gate must
            // read the ini's vsock listener instead.
            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("XRDP_POSTBOOT_RESULT=already");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(copiedContent);
            Assert.IsFalse(copiedContent.Contains("SKIP_CONFIG"), "the skip must not be keyed on package presence");
            StringAssert.Contains(copiedContent,
                "grep -Eq '^[[:space:]]*port[[:space:]]*=[[:space:]]*vsock://' \"$INI\"",
                "the skip gate reads xrdp.ini's vsock listener");

            // The ini lookup must precede the skip gate (the gate greps $INI).
            int iniIdx = copiedContent.IndexOf("INI=/etc/xrdp/xrdp.ini", StringComparison.Ordinal);
            int gateIdx = copiedContent.IndexOf("[[:space:]]*vsock://", StringComparison.Ordinal);
            Assert.IsTrue(iniIdx >= 0 && gateIdx > iniIdx,
                "the xrdp.ini lookup must run before the skip gate checks it");
        }

        [TestMethod]
        public async Task ExecuteAsync_Script_ChrootNeverSkipsConfigWrite_LiveConfiguredProbesBeforeRestarts()
        {
            // The skip gate must be live-mode-only (--chroot always converges
            // the config on disk): a chroot with preinstalled xrdp gets the
            // config written, and a live run whose ini already carries the
            // listener PROBES it first. A restart is only permitted as the
            // last resort inside the gate — when the daemon is active
            // WITHOUT the vsock listener and the chroot half's TCP backup
            // exists to recover into (first boot, no live RDP session can
            // exist yet, so the session-killing concern does not apply). A
            // listener-serving daemon must never be restarted.
            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("XRDP_POSTBOOT_RESULT=already");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(copiedContent);
            int gateIdx = copiedContent.IndexOf(
                "if [ \"$CHROOT_MODE\" = \"0\" ] && [ -n \"$INI\" ] && grep -Eq",
                StringComparison.Ordinal);
            Assert.IsTrue(gateIdx >= 0, "the skip gate must require live mode, a resolved ini, and the vsock listener");

            // The gate's whole block (down to the empty-ini check that ends
            // the short-circuit section): the probe comes first, and every
            // restart in the block is guarded by the no-listener condition.
            int emptyIniIdx = copiedContent.IndexOf("if [ -z \"$INI\" ]", gateIdx, StringComparison.Ordinal);
            Assert.IsTrue(emptyIniIdx > gateIdx, "the skip block must be bounded by the empty-ini check");
            string skipBody = copiedContent.Substring(gateIdx, emptyIniIdx - gateIdx);

            int probeIdx = skipBody.IndexOf("ss -H -l -A vsock", StringComparison.Ordinal);
            Assert.IsTrue(probeIdx >= 0, "the gate probes the live listener before anything else");
            Assert.IsFalse(skipBody.Substring(0, probeIdx).Contains("systemctl restart", StringComparison.Ordinal),
                "the already-configured path must probe before any restart");

            // Every restart in the gate sits under the probe's failure
            // branches (elif/else of the listener check) — i.e. after the
            // probe, never before it.
            Assert.IsTrue(skipBody.LastIndexOf("systemctl restart", StringComparison.Ordinal) > probeIdx,
                "restarts in the gate are recovery-only, on the no-listener branches");
        }
    }
}
