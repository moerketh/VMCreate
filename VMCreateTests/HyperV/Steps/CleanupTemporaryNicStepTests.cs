using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public class CleanupTemporaryNicStepTests
    {
        private Mock<IGuestShell> _shellMock;
        private Mock<ILogger> _loggerMock;
        private CleanupTemporaryNicStep _step;

        [TestInitialize]
        public void Setup()
        {
            _shellMock = new Mock<IGuestShell>();
            _loggerMock = new Mock<ILogger>();
            _step = new CleanupTemporaryNicStep();
        }

        [TestMethod]
        public void Metadata_IsCorrect()
        {
            Assert.AreEqual("Cleanup Temporary NIC", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(830, _step.Order);
            Assert.IsNull(_step.ProgressPhaseId);
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations()));
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenRestoreScriptExists_RunsCleanupInBackground()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(
                    "test -f /var/lib/vmcreate/restore_net.sh \u0026\u0026 echo CLEANUP || echo SKIP",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("CLEANUP\n");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.RunCommandAsync(
                "nohup bash -c 'sleep 2; sudo bash /var/lib/vmcreate/restore_net.sh' \u0026>/dev/null \u0026",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenRestoreScriptAbsent_DoesNothing()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(
                    "test -f /var/lib/vmcreate/restore_net.sh \u0026\u0026 echo CLEANUP || echo SKIP",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("SKIP\n");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("nohup bash")),
                It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
