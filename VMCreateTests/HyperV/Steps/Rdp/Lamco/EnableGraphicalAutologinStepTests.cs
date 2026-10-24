using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public sealed class EnableGraphicalAutologinStepTests
    {
        // Fixture fields — assigned in [TestInitialize] Setup() before every test.
        private EnableGraphicalAutologinStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<EnableGraphicalAutologinStep>> _logger = null!;
        private GalleryItem _item = null!;
        private VmCustomizations _lamcoCustomizations = null!;
        private VmCustomizations _xrdpCustomizations = null!;

        [TestInitialize]
        public void Setup()
        {
            _step = new EnableGraphicalAutologinStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<EnableGraphicalAutologinStep>>();
            _item = new GalleryItem { LinuxDistro = LinuxDistro.Ubuntu, InitialUsername = "ubuntu" };
            _lamcoCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Lamco };
            _xrdpCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Xrdp };
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Enable Graphical Autologin (Wayland)", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(238, _step.Order);
            Assert.AreEqual("Sub_EnableAutologin", _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_TrueForLamcoOnDebianFamily()
        {
            Assert.IsTrue(_step.IsApplicable(_item, _lamcoCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForRpmDistros()
        {
            // Same gate as InstallLamcoRdpStep: --rdp-backend lamco on a
            // non-Debian distro must not enable a passwordless desktop with
            // no RDP server installed.
            var fedoraItem = new GalleryItem { LinuxDistro = LinuxDistro.Fedora, InitialUsername = "user" };
            Assert.IsFalse(_step.IsApplicable(fedoraItem, _lamcoCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForXrdp()
        {
            // xrdp deliberately disables autologin (display :0 at greeter, xrdp owns :10).
            Assert.IsFalse(_step.IsApplicable(_item, _xrdpCustomizations));
        }

        [TestMethod]
        public void IsApplicable_FalseForNone()
        {
            var c = new VmCustomizations { RdpBackend = RdpBackend.None };
            Assert.IsFalse(_step.IsApplicable(_item, c));
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysAndRunsScript()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("AUTOLOGIN_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("wayland-sessions") &&
                    content.Contains("AutomaticLogin") &&
                    content.Contains("enable-linger") &&
                    content.Contains("99-lamco-autologin") &&
                    // greetd (Omarchy 4.x on Arch): [initial_session]
                    // autologin block + Hyprland Lua monitor rule (Omarchy
                    // runs the Lua config — classic hyprland.conf is ignored).
                    content.Contains("has_greetd") &&
                    content.Contains("[initial_session]") &&
                    content.Contains("monitors.lua")),
                It.Is<string>(p => p.StartsWith("/tmp/enable_autologin_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo chown root:root /tmp/enable_autologin_")
                                     && cmd.Contains("sudo chmod 0700 /tmp/enable_autologin_")
                                     && cmd.Contains("sudo bash /tmp/enable_autologin_")
                                     && cmd.Contains("sudo rm -f /tmp/enable_autologin_")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_SubstitutesAutologinUser()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("AUTOLOGIN_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => content.Contains("USER=\"ubuntu\"")),
                It.Is<string>(p => p.StartsWith("/tmp/enable_autologin_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_BlankUsername_SkipsStepInsteadOfRootAutologin()
        {
            // A blank gallery field must SKIP the step with a warning, never
            // configure root graphical autologin (GDM/SDDM mostly refuse it;
            // LightDM — Parrot's historical DM — would happily do it).
            var blankItem = new GalleryItem { LinuxDistro = LinuxDistro.Parrot, InitialUsername = "" };

            await _step.ExecuteAsync(_shell.Object, blankItem, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
                "blank username skips the step — nothing is copied or run");
            _logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("InitialUsername")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_InvalidUsername_ThrowsBeforeCopy()
        {
            // Username goes into a root-run script via naive string.Replace;
            // hostile values must fail host-side validation.
            var hostileItem = new GalleryItem { LinuxDistro = LinuxDistro.Parrot, InitialUsername = "user\"; touch /tmp/pwned" };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, hostileItem, _lamcoCustomizations, _logger.Object, CancellationToken.None));

            _shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Missing_Throws()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("no contract line");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Degraded_LogsWarning()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("WARNING: no display manager detected\nAUTOLOGIN_RESULT=degraded");

            await _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            _logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("DEGRADED")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_SubstitutesConfiguredConsoleResolution()
        {
            // The console resolution from the Settings window (DefaultConsoleResolution
            // → VmCustomizations) is substituted host-side into greetd/Hyprland's
            // Lua monitor rule, GDM's monitors.xml and the kscreen-doctor
            // autostart entry.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("AUTOLOGIN_RESULT=ok");

            var customizations = new VmCustomizations { RdpBackend = RdpBackend.Lamco, ConsoleResolution = "1600x900@60" };
            await _step.ExecuteAsync(_shell.Object, _item, customizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "mode = \"1600x900@60\"",
                "Hyprland Lua rule (greetd/Omarchy path) carries the configured mode");
            StringAssert.Contains(captured, "<width>1600</width>",
                "GDM monitors.xml width");
            StringAssert.Contains(captured, "<height>900</height>",
                "GDM monitors.xml height");
            StringAssert.Contains(captured, "<rate>60.000</rate>",
                "GDM monitors.xml rate uses the three-decimal form");
            StringAssert.Contains(captured, "Exec=kscreen-doctor output.1.mode.1600x900@60",
                "KDE autostart entry forces the configured mode");
            Assert.IsFalse(captured.Contains("__CONSOLE_"),
                "every console token must be substituted before the script reaches the guest");
        }

        [TestMethod]
        public async Task ExecuteAsync_GdmAutologin_CoversDebianDaemonConfAndVersionedMonitorsXml()
        {
            // Live (Kali GNOME): Debian's gdm3 reads /etc/gdm3/daemon.conf —
            // only custom.conf was handled, so autologin was silently skipped.
            // And mutter 50 segfaults on a version-less monitors.xml, killing
            // the autologin session in under a second.
            string? captured = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, _, _) => captured = content);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("AUTOLOGIN_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _item, _lamcoCustomizations, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(captured);
            StringAssert.Contains(captured, "for conf in /etc/gdm3/daemon.conf /etc/gdm3/custom.conf /etc/gdm/custom.conf; do",
                "Debian/Kali gdm3's daemon.conf is configured alongside Ubuntu/Fedora custom.conf");
            StringAssert.Contains(captured, "GDM autologin NOT configured.\"\n        DEGRADED=1",
                "an installed gdm with no config file degrades instead of passing silently");
            StringAssert.Contains(captured, "<monitors version=\"2\">",
                "monitors.xml carries the format version mutter requires");
            Assert.IsFalse(captured.Contains("<monitors>\n"),
                "no version-less monitors.xml is written");
        }

        [TestMethod]
        public async Task ExecuteAsync_InvalidConsoleResolution_ThrowsBeforeCopy()
        {
            // The resolution comes from a hand-editable settings file; a
            // hostile value must fail host-side shape validation — it is
            // substituted into root-run sed/heredoc commands. The item's
            // username is valid so the run gets past the username checks
            // to the console validation.
            var customizations = new VmCustomizations { RdpBackend = RdpBackend.Lamco, ConsoleResolution = "1600x900@60 && reboot" };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _item, customizations, _logger.Object, CancellationToken.None));

            _shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _shell.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
