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
        private Mock<IGuestShell> _shellMock = null!;
        private Mock<ILogger> _loggerMock = null!;
        private SyncTimezoneStep _step = null!;
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
            Assert.IsTrue(_step.SupportsChroot, "timezone is a file change — chroot-capable");
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

        [TestMethod]
        public async Task ExecuteAsync_InChroot_UsesZoneinfoSymlink_NotTimedatectl()
        {
            var chroot = new Mock<IGuestShell>();
            chroot.Setup(s => s.Context).Returns(GuestExecutionContext.Chroot);
            chroot.Setup(s => s.VmName).Returns("TestVM");
            chroot
                .Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("TIMEZONE_FILE_OK");

            await _step.ExecuteAsync(
                chroot.Object,
                new GalleryItem(),
                new VmCustomizations { SyncTimezone = true },
                _loggerMock.Object,
                CancellationToken.None);

            chroot.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd =>
                    cmd.Contains("ln -sf '/usr/share/zoneinfo/") &&
                    cmd.Contains("/etc/localtime") &&
                    cmd.Contains("/etc/timezone")),
                It.IsAny<CancellationToken>()), Times.Once,
                "the chroot branch must set the timezone via files");
            chroot.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("timedatectl")),
                It.IsAny<CancellationToken>()), Times.Never,
                "timedatectl cannot work in a chroot (no systemd manager)");
        }

        [TestMethod]
        public async Task ExecuteAsync_InChroot_MissingConfirmationMarker_Fails()
        {
            var chroot = new Mock<IGuestShell>();
            chroot.Setup(s => s.Context).Returns(GuestExecutionContext.Chroot);
            chroot.Setup(s => s.VmName).Returns("TestVM");
            chroot
                .Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _step.ExecuteAsync(
                    chroot.Object,
                    new GalleryItem(),
                    new VmCustomizations { SyncTimezone = true },
                    _loggerMock.Object,
                    CancellationToken.None));
        }

        private void VerifyLoggerLogged(LogLevel level, string partialMessage)
        {
            _loggerMock.Verify(
                x => x.Log(
                    level,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains(partialMessage)),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }
    }
}
