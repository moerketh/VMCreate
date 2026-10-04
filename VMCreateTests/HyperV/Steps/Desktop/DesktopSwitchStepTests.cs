using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;
using VMCreate.Gallery;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Tests for <see cref="DesktopSwitchStep"/> — the engine behind the
    /// desktop-environment card on the customization page. Customizations-gated
    /// (runs only when a desktop id was picked), catalog-driven, order 231
    /// BEFORE the Auto RDP resolver (232) so the resolver sees the
    /// post-switch default session.
    /// </summary>
    [TestClass]
    public sealed class DesktopSwitchStepTests
    {
        private DesktopSwitchStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<DesktopSwitchStep>> _logger = null!;
        private GalleryItem _kaliItem = null!;
        private GalleryItem _nonKaliItem = null!;
        private VmCustomizations _kdeCustomizations = null!;

        [TestInitialize]
        public void Setup()
        {
            _step = new DesktopSwitchStep(new[] { new KaliDesktopCatalog() });
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<DesktopSwitchStep>>();
            _kaliItem = new GalleryItem { LinuxDistro = LinuxDistro.Kali };
            _nonKaliItem = new GalleryItem { LinuxDistro = LinuxDistro.Ubuntu };
            _kdeCustomizations = new VmCustomizations { DesktopEnvironmentId = "kde" };
        }

        private void SetupScriptResult(string output)
            => _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(output);

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Switch Desktop Environment", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(231, _step.Order);

            // ProgressPhaseId === DeployPhaseId (IDistributionOptionMetadata
            // pattern shared with FlareVmStabilizationStep).
            Assert.AreEqual("Sub_DesktopSwitch", _step.ProgressPhaseId);
            Assert.AreEqual(_step.DeployPhaseId, _step.ProgressPhaseId);
        }

        [TestMethod]
        public void DeployCardMetadata_IsCorrect()
        {
            Assert.AreEqual("Desktop Environment", _step.CardTitle);
            Assert.AreEqual("Switch desktop environment", _step.Label);
            Assert.AreEqual("Switch Desktop Environment", _step.DeployTitle);
            Assert.AreEqual("Desktop24", _step.DeployIconName);
            Assert.AreEqual(231, _step.DeployOrder);
            Assert.IsFalse(_step.IsOptional, "required steps render deploy cards that cannot be user-disabled");
            Assert.IsFalse(_step.DefaultEnabled, "no desktop is switched unless the user picks one");
            Assert.IsNull(_step.DeployCompletionInfo);
        }

        [TestMethod]
        public void IsApplicable_RequiresDesktopSelection_AndCatalogSupport()
        {
            // No desktop picked → never runs (keep-default is a no-op).
            Assert.IsFalse(_step.IsApplicable(_kaliItem, new VmCustomizations()));
            Assert.IsFalse(_step.IsApplicable(_kaliItem, null));

            // Picked desktop offered by the item's catalog → runs under
            // every RDP backend (Auto/Xrdp/Lamco/None).
            Assert.IsTrue(_step.IsApplicable(_kaliItem, _kdeCustomizations));
            Assert.IsTrue(_step.IsApplicable(_kaliItem, new VmCustomizations { DesktopEnvironmentId = "kde", RdpBackend = RdpBackend.Xrdp }));
            Assert.IsTrue(_step.IsApplicable(_kaliItem, new VmCustomizations { DesktopEnvironmentId = "kde", RdpBackend = RdpBackend.Lamco }));

            // Unknown desktop id (catalog/image divergence) → refuse.
            Assert.IsFalse(_step.IsApplicable(_kaliItem, new VmCustomizations { DesktopEnvironmentId = "enlightenment" }));

            // Desktop selected on a distro without a catalog → refuse.
            Assert.IsFalse(_step.IsApplicable(_nonKaliItem, _kdeCustomizations));
        }

        [TestMethod]
        public void IsVisibleFor_TracksCatalogPresence()
        {
            Assert.IsTrue(_step.IsVisibleFor(_kaliItem), "Kali offers desktop switching");
            Assert.IsFalse(_step.IsVisibleFor(_nonKaliItem), "No catalog for this distro — card and step hidden");
            Assert.IsFalse(_step.IsVisibleFor(null));
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Ok_Succeeds()
        {
            SetupScriptResult("Setting up kali-desktop-kde...\nDESKTOP_SWITCH_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _kaliItem, _kdeCustomizations, _logger.Object, CancellationToken.None);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Degraded_LogsWarning()
        {
            // "degraded": the desktop itself switched; a side task (e.g. the
            // session-manager alternative) failed — warn, don't fail.
            SetupScriptResult("update-alternatives failed\nDESKTOP_SWITCH_RESULT=degraded");

            await _step.ExecuteAsync(_shell.Object, _kaliItem, _kdeCustomizations, _logger.Object, CancellationToken.None);

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
            // Zero exit with no DESKTOP_SWITCH_RESULT line = fabricated success —
            // must throw.
            SetupScriptResult("some output but no terminal contract line");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _kaliItem, _kdeCustomizations, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        public async Task ExecuteAsync_UnknownDesktop_Throws()
        {
            // The catalog no longer offers the stored id — never guess.
            var stale = new VmCustomizations { DesktopEnvironmentId = "plasma5" };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _kaliItem, stale, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        public void ParseResultLine_LastOccurrenceWins()
        {
            // internal static — directly testable for the parser contract.
            Assert.AreEqual("ok", DesktopSwitchStep.ParseResultLine("DESKTOP_SWITCH_RESULT=degraded\nDESKTOP_SWITCH_RESULT=ok"));
            Assert.AreEqual("degraded", DesktopSwitchStep.ParseResultLine(" DESKTOP_SWITCH_RESULT=degraded \n"));
            Assert.IsNull(DesktopSwitchStep.ParseResultLine("no contract line here"));
            Assert.IsNull(DesktopSwitchStep.ParseResultLine("DESKTOP_SWITCH_RESULT=explosive"));
        }

        [TestMethod]
        public async Task ExecuteAsync_ShipsParameterizedScriptWithHardening()
        {
            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, path, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            SetupScriptResult("DESKTOP_SWITCH_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _kaliItem, _kdeCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.IsAny<string>(),
                It.Is<string>(p => p.StartsWith("/tmp/desktop_switch_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);
            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("chown root:root") && cmd.Contains("chmod 0700") && cmd.Contains("rm -f")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);

            Assert.IsNotNull(copiedContent);
            // The generic script plus the injected parameter block naming
            // the KDE option from the catalog.
            StringAssert.Contains(copiedContent, "kali-desktop-kde", "installs the KDE desktop metapackage");
            StringAssert.Contains(copiedContent, "kali-desktop-xfce", "purges the stock XFCE desktop");
            StringAssert.Contains(copiedContent, "startplasma-wayland", "makes the Plasma Wayland session default");
            StringAssert.Contains(copiedContent, "DESKTOP_SWITCH_RESULT=", "carries the result-line contract");
            // The sddm/DM enable must be verified, not silently swallowed: a
            // bare `systemctl enable <dm> 2>/dev/null || true` shipped machines
            // that booted to a text console because the display-manager alias
            // never existed (TEST_20260910*, pinned by the retired
            // KaliKdeSwitchStep tests — ported here for the generic step).
            Assert.IsFalse(copiedContent.Contains("systemctl enable sddm.service 2>/dev/null || true"),
                "DM enable must not be silently swallowed");
            StringAssert.Contains(copiedContent, "systemctl enable \"$DESKTOP_DISPLAY_MANAGER.service\"",
                "the target display manager is explicitly enabled");
            StringAssert.Contains(copiedContent, "/etc/systemd/system/display-manager.service",
                "the display-manager alias is verified to exist");
            StringAssert.Contains(copiedContent, "graphical.target",
                "the default target is verified to be graphical");
            // Parameters land directly below the shebang.
            var secondLine = copiedContent!.Split('\n')[1];
            StringAssert.Contains(secondLine, "DESKTOP_PACKAGES=", "parameter block is injected below the shebang");
        }

        [TestMethod]
        public void BuildScript_RejectsUnsafeCatalogValues()
        {
            // Catalog data is trusted offline data, but the injection must
            // still refuse values that would break shell quoting.
            var hostile = new DesktopEnvironmentOption
            {
                Id = "evil",
                DisplayName = "Evil",
                Packages = new List<string> { "pkg'; rm -rf /" }
            };

            var ex = Assert.Throws<InvalidOperationException>(() => DesktopSwitchStep.BuildScript(hostile, DesktopPackageManager.Apt));
            StringAssert.Contains(ex.Message, "cannot be safely passed");
        }

        // ── Package-manager dispatch (apt vs pacman) ─────────────────────

        [TestMethod]
        public void BuildScript_DispatchesScriptByPackageManager()
        {
            // The catalog's PackageManager picks the embedded script family:
            // apt distros (Kali) get the dpkg/debconf script; the Arch
            // lineage (BlackArch) gets the pacman script.
            var option = new DesktopEnvironmentOption
            {
                Id = "kde",
                DisplayName = "KDE Plasma",
                Packages = new List<string> { "plasma", "sddm" }
            };

            string apt = DesktopSwitchStep.BuildScript(option, DesktopPackageManager.Apt);
            string pacman = DesktopSwitchStep.BuildScript(option, DesktopPackageManager.Pacman);

            StringAssert.Contains(apt, "apt-get", "the apt script drives apt/dpkg");
            StringAssert.Contains(pacman, "pacman", "the pacman script drives pacman");
            // Arch has no dpkg/debconf / update-alternatives machinery.
            Assert.IsFalse(pacman.Contains("debconf-set-selections"),
                "the pacman script must not reference debconf (not present on Arch)");
            // Both share the same parameter-injection + result contract.
            foreach (var script in new[] { apt, pacman })
            {
                StringAssert.Contains(script, "DESKTOP_PACKAGES='plasma sddm'", "packages are injected");
                StringAssert.Contains(script, "DESKTOP_SWITCH_RESULT=", "carries the result-line contract");
            }
        }

        [TestMethod]
        public void BuildScript_UnsupportedPackageManager_Throws()
        {
            // A catalog must never advertise a package manager that has no
            // shipped switch script — fail loudly rather than guessing.
            var option = new DesktopEnvironmentOption
            {
                Id = "kde",
                DisplayName = "KDE Plasma",
                Packages = new List<string> { "plasma" }
            };

            var ex = Assert.Throws<System.NotSupportedException>(() =>
                DesktopSwitchStep.BuildScript(option, DesktopPackageManager.Dnf));
            StringAssert.Contains(ex.Message, "No desktop-switch script");
        }

        [TestMethod]
        public async Task ExecuteAsync_PacmanItem_RunsPacmanScriptWithInjectedPackages()
        {
            // End-to-end for the Arch/BlackArch lineage: the BlackArch catalog
            // advertises Pacman, so the step must ship the pacman script with
            // the chosen desktop's packages injected.
            var step = new DesktopSwitchStep(new IDesktopEnvironmentCatalog[] { new BlackArchDesktopCatalog() });
            var blackArchItem = new GalleryItem
            {
                Name = "BlackArch",
                LinuxDistro = LinuxDistro.Arch,
                Tags = new List<string> { "blackarch" }
            };
            var customizations = new VmCustomizations { DesktopEnvironmentId = "gnome" };

            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, path, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            SetupScriptResult("DESKTOP_SWITCH_RESULT=ok");

            await step.ExecuteAsync(_shell.Object, blackArchItem, customizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(copiedContent);
            StringAssert.Contains(copiedContent, "pacman", "the BlackArch catalog runs the pacman script");
            StringAssert.Contains(copiedContent, "gnome", "the GNOME packages are injected");
            Assert.IsFalse(copiedContent.Contains("apt-get"),
                "the pacman lineage must not run the apt script");
        }

        [TestMethod]
        public void IsApplicable_BlackArchItem_CatalogResolvesArchDesktops()
        {
            var step = new DesktopSwitchStep(new IDesktopEnvironmentCatalog[] { new BlackArchDesktopCatalog() });
            var blackArchItem = new GalleryItem
            {
                Name = "BlackArch",
                LinuxDistro = LinuxDistro.Arch,
                Tags = new List<string> { "blackarch" }
            };

            Assert.IsTrue(step.IsVisibleFor(blackArchItem), "the desktop card applies to the BlackArch item");
            Assert.IsTrue(step.IsApplicable(blackArchItem, new VmCustomizations { DesktopEnvironmentId = "xfce" }));
            Assert.IsTrue(step.IsApplicable(blackArchItem, new VmCustomizations { DesktopEnvironmentId = "no-desktop" }));
            Assert.IsFalse(step.IsApplicable(blackArchItem, null), "no desktop picked → no switch");
            Assert.IsFalse(step.IsApplicable(blackArchItem, new VmCustomizations { DesktopEnvironmentId = "enlightenment" }),
                "unknown id → refuse");
        }
    }
}
