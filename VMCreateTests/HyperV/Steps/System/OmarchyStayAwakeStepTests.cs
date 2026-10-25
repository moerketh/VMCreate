using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Tests for <see cref="OmarchyStayAwakeStep"/> — deployed Omarchy VMs must
    /// not idle into the screensaver/lock screen (a locked Hyprland shows a
    /// black console and blanks the RDP picture).
    /// </summary>
    [TestClass]
    public sealed class OmarchyStayAwakeStepTests
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
            var step = new OmarchyStayAwakeStep();

            Assert.AreEqual(CustomizationPhase.PostBoot, step.Phase);
            Assert.AreEqual(StepPlatform.Linux, step.Platform);
            Assert.IsTrue(step.Order < new StartGraphicalSessionStep().Order,
                "the flag must exist before the session start step brings the desktop up");
            Assert.IsTrue(step.Order < new RemovePasswordlessSudoStep().Order, "inside the post-boot SSH window");
        }

        [TestMethod]
        public void Gates_OnOmarchyTag_IndependentOfRdpBackend()
        {
            var step = new OmarchyStayAwakeStep();

            foreach (var backend in Enum.GetValues<RdpBackend>())
                Assert.IsTrue(step.IsApplicable(Omarchy(), new VmCustomizations { RdpBackend = backend }),
                    $"Omarchy must stay awake under every RDP backend ({backend})");
            Assert.IsTrue(step.IsApplicable(Omarchy(), null));

            Assert.IsFalse(step.IsApplicable(new GalleryItem { Name = "Kali", LinuxDistro = LinuxDistro.Debian }, new VmCustomizations()));
            Assert.IsFalse(step.IsApplicable(null, null));
        }

        [TestMethod]
        public async Task Execute_SetsOmarchysOwnStayAwakeFlag_AsTheDeployUser()
        {
            string? command = null;
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, CancellationToken>((c, _) => command = c)
                  .ReturnsAsync("STAY_AWAKE_RESULT=ok\n");

            await new OmarchyStayAwakeStep().ExecuteAsync(_shell.Object, Omarchy(), new VmCustomizations(), _logger.Object, CancellationToken.None);

            Assert.IsNotNull(command);
            StringAssert.Contains(command, "omarchy-toggle-idle stay-awake", "prefer Omarchy's own switch");
            StringAssert.Contains(command, "touch \"$HOME/.local/state/omarchy/indicators/stay-awake\"",
                "fallback: the state file the quickshell idle service watches");
            Assert.IsFalse(command.Contains("sudo"),
                "the flag lives in the deploy user's home and must be owned by that user — no sudo");
            VerifyLogged(LogLevel.Warning, Times.Never());
        }

        [TestMethod]
        public async Task Execute_FailureIsBestEffort_WarnsAndDoesNotThrow()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("STAY_AWAKE_RESULT=failed\n");
            await new OmarchyStayAwakeStep().ExecuteAsync(_shell.Object, Omarchy(), new VmCustomizations(), _logger.Object, CancellationToken.None);

            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new Exception("ssh exited 255"));
            await new OmarchyStayAwakeStep().ExecuteAsync(_shell.Object, Omarchy(), new VmCustomizations(), _logger.Object, CancellationToken.None);

            VerifyLogged(LogLevel.Warning, Times.Exactly(2));
        }

        private void VerifyLogged(LogLevel level, Times times) =>
            _logger.Verify(l => l.Log(level, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);
    }
}
