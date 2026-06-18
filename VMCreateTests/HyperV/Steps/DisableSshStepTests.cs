using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public class DisableSshStepTests
    {
        private Mock<IGuestShell> _shellMock;
        private Mock<ILogger> _loggerMock;
        private DisableSshStep _step;

        [TestInitialize]
        public void Setup()
        {
            _shellMock = new Mock<IGuestShell>();
            _loggerMock = new Mock<ILogger>();
            _step = new DisableSshStep();
        }

        [TestMethod]
        public void Metadata_IsCorrect()
        {
            Assert.AreEqual("Restore SSH State", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(900, _step.Order);
            Assert.AreEqual("Sub_RestoreSsh", _step.ProgressPhaseId);
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations()));
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenMarkerFileExists_DisablesSsh()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(
                    "test -f /var/lib/vmcreate/.ssh_was_disabled \u0026\u0026 echo RESTORE || echo KEEP",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("RESTORE\n");

            _shellMock
                .Setup(s => s.RunCommandAsync(
                    It.Is<string>(cmd => cmd.Contains("systemctl disable ssh")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("SSH disabled for next boot");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("systemctl disable ssh")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenMarkerFileAbsent_LeavesSshAsIs()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(
                    "test -f /var/lib/vmcreate/.ssh_was_disabled \u0026\u0026 echo RESTORE || echo KEEP",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("KEEP\n");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("systemctl disable ssh")),
                It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
