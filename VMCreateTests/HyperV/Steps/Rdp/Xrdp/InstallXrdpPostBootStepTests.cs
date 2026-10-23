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
        public async Task ExecuteAsync_Script_ChrootNeverSkipsConfigWrite_LiveConfiguredNeverRestarts()
        {
            // The skip gate must be live-mode-only (--chroot always converges
            // the config on disk) and its body must never restart xrdp (the
            // session-killing restart): a chroot with preinstalled xrdp gets
            // the config written, and a live run whose ini already carries
            // the listener only probes it.
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
            // the short-circuit section) runs to its exits without a restart.
            int emptyIniIdx = copiedContent.IndexOf("if [ -z \"$INI\" ]", gateIdx, StringComparison.Ordinal);
            Assert.IsTrue(emptyIniIdx > gateIdx, "the skip block must be bounded by the empty-ini check");
            string skipBody = copiedContent.Substring(gateIdx, emptyIniIdx - gateIdx);
            Assert.IsFalse(skipBody.Contains("systemctl restart", StringComparison.Ordinal),
                "the already-configured path must probe, never restart — a restart kills live RDP sessions");
        }
    }
}
