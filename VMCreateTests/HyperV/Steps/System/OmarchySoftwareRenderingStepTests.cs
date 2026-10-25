using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Tests for <see cref="OmarchySoftwareRenderingStep"/> — Omarchy on Hyper-V
    /// renders in software, so the deploy installs an Omarchy toggle flag that
    /// turns animations, blur, shadows and window transparency off.
    /// </summary>
    [TestClass]
    public sealed class OmarchySoftwareRenderingStepTests
    {
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger> _logger = null!;

        private static GalleryItem Omarchy() => new()
        {
            Name = "Omarchy",
            LinuxDistro = LinuxDistro.Arch,
            InitialUsername = "omarchy",
            Tags = new List<string> { "omarchy" },
        };

        [TestInitialize]
        public void Setup()
        {
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger>();
            _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        }

        [TestMethod]
        public void Metadata_RunsPostBootBeforeTheGraphicalSessionStart()
        {
            var step = new OmarchySoftwareRenderingStep();

            Assert.AreEqual(CustomizationPhase.PostBoot, step.Phase);
            Assert.AreEqual(StepPlatform.Linux, step.Platform);
            Assert.IsTrue(step.Order < new StartGraphicalSessionStep().Order,
                "the flag must exist before the session start step brings the desktop up");
            Assert.IsTrue(step.Order < new RemovePasswordlessSudoStep().Order, "inside the post-boot SSH window");
        }

        [TestMethod]
        public void Gates_OnOmarchyTag_IndependentOfRdpBackend()
        {
            var step = new OmarchySoftwareRenderingStep();

            foreach (var backend in Enum.GetValues<RdpBackend>())
                Assert.IsTrue(step.IsApplicable(Omarchy(), new VmCustomizations { RdpBackend = backend }),
                    $"the console renders in software under every RDP backend ({backend})");
            Assert.IsTrue(step.IsApplicable(Omarchy(), null));

            Assert.IsFalse(step.IsApplicable(new GalleryItem { Name = "Kali", LinuxDistro = LinuxDistro.Debian }, new VmCustomizations()));
            Assert.IsFalse(step.IsApplicable(null, null));
        }

        [TestMethod]
        public async Task Execute_WritesTheOmarchyToggleFlag_AsTheDeployUser()
        {
            string? command = null;
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, CancellationToken>((c, _) => command = c)
                  .ReturnsAsync("OMARCHY_SWRENDER_RESULT=ok\n");

            await new OmarchySoftwareRenderingStep().ExecuteAsync(_shell.Object, Omarchy(), new VmCustomizations(), _logger.Object, CancellationToken.None);

            Assert.IsNotNull(command);
            StringAssert.Contains(command, "> \"$HOME/.local/state/omarchy/toggles/hypr/vmcreate-hyperv.lua\"",
                "Omarchy's permanent-flag directory: loaded after the user config, removable with omarchy-hyprland-toggle");
            StringAssert.Contains(command, "[ ! -f \"$HOME/.config/hypr/hyprland.lua\" ]",
                "only Omarchy's Lua config reads a .lua flag");
            Assert.IsFalse(command.Contains("sudo"),
                "the flag lives in the deploy user's home and must be owned by that user — no sudo");

            // The shipped payload is exactly the flag content.
            var b64 = Regex.Match(command, @"printf '%s' '([A-Za-z0-9+/=]+)' \| base64 -d").Groups[1].Value;
            string lua = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            Assert.AreEqual(OmarchySoftwareRenderingStep.FlagContent, lua);
            StringAssert.Contains(lua, "animations = { enabled = false }");
            StringAssert.Contains(lua, "blur = { enabled = false }");
            StringAssert.Contains(lua, "shadow = { enabled = false }");
            StringAssert.Contains(lua, "o.window({ tag = \"default-opacity\" }, { opacity = \"1 1\" })",
                "overrides Omarchy's default-opacity rule (0.985 0.96) — later rules win");
            VerifyLogged(LogLevel.Warning, Times.Never());
        }

        [TestMethod]
        public async Task Execute_OlderOmarchyWithoutLuaConfig_IsSkippedWithoutWarning()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("OMARCHY_SWRENDER_RESULT=skipped\n");

            await new OmarchySoftwareRenderingStep().ExecuteAsync(_shell.Object, Omarchy(), new VmCustomizations(), _logger.Object, CancellationToken.None);

            VerifyLogged(LogLevel.Warning, Times.Never());
            VerifyLogged(LogLevel.Information, Times.Once());
        }

        [TestMethod]
        public async Task Execute_FailureIsBestEffort_WarnsAndDoesNotThrow()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("OMARCHY_SWRENDER_RESULT=failed\n");
            await new OmarchySoftwareRenderingStep().ExecuteAsync(_shell.Object, Omarchy(), new VmCustomizations(), _logger.Object, CancellationToken.None);

            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new Exception("ssh exited 255"));
            await new OmarchySoftwareRenderingStep().ExecuteAsync(_shell.Object, Omarchy(), new VmCustomizations(), _logger.Object, CancellationToken.None);

            VerifyLogged(LogLevel.Warning, Times.Exactly(2));
        }

        private void VerifyLogged(LogLevel level, Times times) =>
            _logger.Verify(l => l.Log(level, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);
    }
}
