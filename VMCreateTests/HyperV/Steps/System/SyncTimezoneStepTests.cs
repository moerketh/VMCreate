using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public class SyncTimezoneStepTests
    {
        private Mock<IGuestShell> _shellMock;
        private Mock<ILogger> _loggerMock;
        private SyncTimezoneStep _step;

        [TestInitialize]
        public void Setup()
        {
            _shellMock = new Mock<IGuestShell>();
            _loggerMock = new Mock<ILogger>();
            _step = new SyncTimezoneStep();
        }

        [TestMethod]
        public void Metadata_IsCorrect()
        {
            Assert.AreEqual("Sync Timezone", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(100, _step.Order);
            Assert.AreEqual("Sub_SyncTimezone", _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_WhenSyncTimezoneTrue_ReturnsTrue()
        {
            var customizations = new VmCustomizations { SyncTimezone = true };
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), customizations));
        }

        [TestMethod]
        public void IsApplicable_WhenSyncTimezoneFalse_ReturnsFalse()
        {
            var customizations = new VmCustomizations { SyncTimezone = false };
            Assert.IsFalse(_step.IsApplicable(new GalleryItem(), customizations));
        }

        [TestMethod]
        public async Task ExecuteAsync_SetsTimezoneAndLogs()
        {
            var hostTz = TimeZoneInfo.Local;
            _shellMock
                .Setup(s => s.RunCommandAsync(
                    It.Is<string>(cmd => cmd.Contains("timedatectl set-timezone") && cmd.Contains(hostTz.Id)),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("Timezone set");

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                new VmCustomizations { SyncTimezone = true },
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("timedatectl set-timezone")),
                It.IsAny<CancellationToken>()), Times.Once);

            VerifyLoggerLogged(LogLevel.Information, "Syncing timezone");
            VerifyLoggerLogged(LogLevel.Information, "Timezone synced to host");
        }

        private void VerifyLoggerLogged(LogLevel level, string partialMessage)
        {
            _loggerMock.Verify(
                x => x.Log(
                    level,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(partialMessage)),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce);
        }
    }
}
