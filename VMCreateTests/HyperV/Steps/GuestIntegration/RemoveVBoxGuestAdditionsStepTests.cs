using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public class RemoveVBoxGuestAdditionsStepTests
    {
        private Mock<IGuestShell> _shellMock;
        private Mock<ILogger> _loggerMock;
        private RemoveVBoxGuestAdditionsStep _step;

        [TestInitialize]
        public void Setup()
        {
            _shellMock = new Mock<IGuestShell>();
            _loggerMock = new Mock<ILogger>();
            _step = new RemoveVBoxGuestAdditionsStep();
        }

        [TestMethod]
        public void Metadata_IsCorrect()
        {
            Assert.AreEqual("Remove VirtualBox Guest Additions", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(50, _step.Order);
            Assert.AreEqual("Sub_RemoveVBox", _step.ProgressPhaseId);
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations()));
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysRemovalScript_AndRunsIt()
        {
            string capturedContent = null;
            string capturedGuestPath = null;

            _shellMock
                .Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((content, path, _) =>
                {
                    capturedContent = content;
                    capturedGuestPath = path;
                })
                .Returns(Task.CompletedTask);

            _shellMock
                .Setup(s => s.RunCommandAsync(
                    "sudo bash /tmp/remove_vbox.sh && sudo rm -f /tmp/remove_vbox.sh",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("No VirtualBox Guest Additions found");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            Assert.AreEqual("/tmp/remove_vbox.sh", capturedGuestPath);
            Assert.IsNotNull(capturedContent);
            StringAssert.Contains(capturedContent, "#!/bin/bash");
            StringAssert.Contains(capturedContent, "vboxguest");

            _shellMock.Verify(s => s.CopyContentAsync(
                It.Is<string>(c => c.Contains("#!/bin/bash")),
                "/tmp/remove_vbox.sh",
                It.IsAny<CancellationToken>()), Times.Once);

            _shellMock.Verify(s => s.RunCommandAsync(
                "sudo bash /tmp/remove_vbox.sh && sudo rm -f /tmp/remove_vbox.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
