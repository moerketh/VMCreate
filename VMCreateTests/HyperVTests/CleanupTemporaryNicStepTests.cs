using Microsoft.Extensions.Logging;
using Moq;
using VMCreate;

namespace VMCreate.Tests
{
    [TestClass]
    public sealed class CleanupTemporaryNicStepTests
    {
        private CleanupTemporaryNicStep _step;
        private Mock<IGuestShell> _shell;
        private Mock<ILogger> _logger;
        private GalleryItem _item;
        private VmCustomizations _customizations;

        [TestInitialize]
        public void Setup()
        {
            _step = new CleanupTemporaryNicStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger>();
            _item = new GalleryItem();
            _customizations = new VmCustomizations();
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Cleanup Temporary NIC", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(830, _step.Order);
            Assert.IsTrue(_step.IsApplicable(_item, _customizations));
        }

        [TestMethod]
        public async Task ExecuteAsync_RunsCleanupScript_WhenPresent()
        {
            _shell.SetupSequence(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("CLEANUP")
                .ReturnsAsync("Temporary NIC configuration removed");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudo bash")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_NoOp_WhenScriptAbsent()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("SKIP");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            // Should only call RunCommandAsync once (the script check), not the cleanup
            _shell.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
