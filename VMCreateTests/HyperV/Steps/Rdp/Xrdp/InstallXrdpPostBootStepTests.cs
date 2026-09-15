using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Tests for <see cref="InstallXrdpPostBootStep"/> — the Auto-backend
    /// backfill: under <see cref="RdpBackend.Auto"/> the chroot xrdp
    /// install is skipped (no VMCREATE_XRDP KVP), so when the resolver
    /// picks xrdp this step installs it over SSH. It also runs as a
    /// safety net for explicit <see cref="RdpBackend.Xrdp"/> deployments
    /// (one dpkg -s verifies the chroot install).
    /// </summary>
    [TestClass]
    public sealed class InstallXrdpPostBootStepTests
    {
        private InstallXrdpPostBootStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<InstallXrdpPostBootStep>> _logger = null!;
        private GalleryItem _item = null!;
        private VmCustomizations _xrdpCustomizations = null!;
        [TestInitialize]
        public void Setup()
        {
            _step = new InstallXrdpPostBootStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<InstallXrdpPostBootStep>>();
            _item = new GalleryItem { LinuxDistro = LinuxDistro.Kali };
            _xrdpCustomizations = new VmCustomizations { RdpBackend = RdpBackend.Xrdp };
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Install xrdp (post-boot)", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(236, _step.Order);
            Assert.AreEqual("Sub_InstallXrdpPostBoot", _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_OnlyForXrdpBackend()
        {
            // Runs for explicit Xrdp AND post-Auto-resolution (the resolver
            // mutates the backend in place before this step's gate is read).
            Assert.IsTrue(_step.IsApplicable(_item, _xrdpCustomizations));

            foreach (var backend in new[] { RdpBackend.Auto, RdpBackend.Lamco, RdpBackend.None })
                Assert.IsFalse(_step.IsApplicable(_item, new VmCustomizations { RdpBackend = backend }),
                    $"IsApplicable must be false for {backend}");

            Assert.IsFalse(_step.IsApplicable(_item, null));
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Installed_Succeeds()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("Reading package lists...\nXRDP_POSTBOOT_RESULT=installed");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Already_Succeeds()
        {
            // The chroot install (explicit xrdp deployments) leaves xrdp
            // present — the script short-circuits with "already".
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("XRDP_POSTBOOT_RESULT=already");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Missing_Throws()
        {
            // A zero-exit run with NO result line means the script was
            // swallowed — fabricated-success class; it must throw so the
            // deployment reports failure, never a silent green.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("some output but no terminal contract line");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        public async Task ExecuteAsync_ShipsScriptWithUniformGuestHardening()
        {
            // Same guest-script contract as every shipped step: GUID path in
            // /tmp, chown root:root, chmod 0700, bash, rm -f.
            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, path, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("XRDP_POSTBOOT_RESULT=already");

            await _step.ExecuteAsync(_shell.Object, _item, _xrdpCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(c => c.Contains("apt-get") || c.Contains("dnf") || c.Contains("pacman")),
                It.Is<string>(p => p.StartsWith("/tmp/install_xrdp_postboot_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);

            Assert.IsNotNull(copiedContent, "script content must reach the guest");
            StringAssert.Contains(copiedContent, "XRDP_POSTBOOT_RESULT=", "script carries the result-line contract");
        }
    }
}
