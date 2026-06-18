using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public class RemoveAutomationUserStepTests
    {
        private Mock<IGuestShell> _shellMock;
        private Mock<ILogger> _loggerMock;
        private RemoveAutomationUserStep _step;

        [TestInitialize]
        public void Setup()
        {
            _shellMock = new Mock<IGuestShell>();
            _loggerMock = new Mock<ILogger>();
            _step = new RemoveAutomationUserStep();
        }

        [TestMethod]
        public void Metadata_IsCorrect()
        {
            Assert.AreEqual("Remove Automation User", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(850, _step.Order);
            Assert.IsNull(_step.ProgressPhaseId);
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations()));
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenUserExists_SchedulesCleanupService()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(
                    It.Is<string>(cmd => cmd.Contains("id vmcreate")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("EXISTS\n");

            string cleanupCommand = null;
            _shellMock
                .Setup(s => s.RunCommandAsync(
                    It.Is<string>(cmd => cmd.Contains("vmcreate-cleanup.service")),
                    It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((cmd, _) => cleanupCommand = cmd)
                .ReturnsAsync("");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            Assert.IsNotNull(cleanupCommand);
            StringAssert.Contains(cleanupCommand, "vmcreate-cleanup.service");
            StringAssert.Contains(cleanupCommand, "systemctl enable vmcreate-cleanup.service");
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenUserAbsent_DoesNothing()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(
                    "id vmcreate >/dev/null 2>&1 \u0026\u0026 echo EXISTS || echo ABSENT",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("ABSENT\n");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("vmcreate-cleanup.service")),
                It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
