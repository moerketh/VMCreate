using Microsoft.Extensions.Logging;
using Moq;
using VMCreate;

namespace VMCreate.Tests
{
    [TestClass]
    public sealed class RemoveAutomationUserStepTests
    {
        private RemoveAutomationUserStep _step;
        private Mock<IGuestShell> _shell;
        private Mock<ILogger> _logger;
        private GalleryItem _item;
        private VmCustomizations _customizations;

        [TestInitialize]
        public void Setup()
        {
            _step = new RemoveAutomationUserStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger>();
            _item = new GalleryItem();
            _customizations = new VmCustomizations();
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Remove Automation User", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(850, _step.Order);
            Assert.IsTrue(_step.IsApplicable(_item, _customizations));
        }

        [TestMethod]
        public async Task ExecuteAsync_RemovesUser_WhenExists()
        {
            _shell.SetupSequence(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("EXISTS")
                .ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("sudoers.d/vmcreate") && cmd.Contains("vmcreate-cleanup.service") && cmd.StartsWith("sudo bash -c")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_NoOp_WhenUserAbsent()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("ABSENT");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            // Should only call RunCommandAsync once (the user check)
            _shell.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
