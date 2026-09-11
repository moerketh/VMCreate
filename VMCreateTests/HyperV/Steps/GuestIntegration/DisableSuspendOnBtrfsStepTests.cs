using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public sealed class DisableSuspendOnBtrfsStepTests
    {
        private DisableSuspendOnBtrfsStep _step;
        private Mock<IGuestShell> _shellMock;
        private Mock<ILogger> _loggerMock;

        [TestInitialize]
        public void Setup()
        {
            _step = new DisableSuspendOnBtrfsStep();
            _shellMock = new Mock<IGuestShell>();
            _shellMock.Setup(s => s.VmName).Returns("TestVM");
            _loggerMock = new Mock<ILogger>();
        }

        [TestMethod]
        public void Metadata_IsCorrect()
        {
            Assert.AreEqual("Disable Suspend on btrfs", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(200, _step.Order);
            Assert.AreEqual("Sub_DisableSuspendOnBtrfs", _step.ProgressPhaseId);
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations()));
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysAndRunsScript()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("btrfs suspend disable complete");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => content.Contains("btrfs")),
                "/tmp/disable_suspend_btrfs.sh",
                It.IsAny<CancellationToken>()), Times.Once);

            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd =>
                    cmd.Contains("sudo bash /tmp/disable_suspend_btrfs.sh") &&
                    cmd.Contains("sudo rm -f /tmp/disable_suspend_btrfs.sh")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptDetectsBtrfs()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(string.Empty);

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("findmnt") &&
                    content.Contains("FSTYPE") &&
                    content.Contains("btrfs")),
                "/tmp/disable_suspend_btrfs.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptDisablesSuspendOnBtrfs()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(string.Empty);

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("AllowSuspend=no") &&
                    content.Contains("AllowHibernation=no") &&
                    content.Contains("AllowHybridSleep=no") &&
                    content.Contains("AllowSuspendThenHibernate=no")),
                "/tmp/disable_suspend_btrfs.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptCreatesDropInDir()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(string.Empty);

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("sleep.conf.d") &&
                    content.Contains("99-disable-suspend.conf")),
                "/tmp/disable_suspend_btrfs.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptNormalizesLineEndings()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(string.Empty);

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations(),
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.CopyContentAsync(
                It.Is<string>(content => !content.Contains("\r\n")),
                "/tmp/disable_suspend_btrfs.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public void IsApplicable_AlwaysReturnsTrue()
        {
            Assert.IsTrue(_step.IsApplicable(null, null));
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations()));
        }
    }
}
