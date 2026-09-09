using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Threading;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Tests for <see cref="DistroDetector"/>, the runtime /etc/os-release
    /// re-verification used as the Lamco install's distro gate. The
    /// classification order matters: exact-ID checks must ALL run before
    /// ID_LIKE fallbacks (Parrot ships ID=parrot + ID_LIKE=debian and was
    /// previously misclassified as Debian).
    /// </summary>
    [TestClass]
    public sealed class DistroDetectorTests
    {
        private static (LinuxDistro? result, Mock<IGuestShell> shell) DetectWith(string osRelease)
        {
            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.VmName).Returns("TestVM");
            shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(osRelease);
            var result = DistroDetector.DetectAsync(shell.Object, CancellationToken.None).GetAwaiter().GetResult();
            return (result, shell);
        }

        [TestMethod]
        public void DetectAsync_ParrotIdWithDebianIdLike_ClassifiesParrot()
        {
            // The trap: Parrot 7 ships ID=parrot with ID_LIKE=debian. The old
            // per-distro family checks matched ID_LIKE=debian first, so the
            // exact parrot branch was unreachable.
            var (result, _) = DetectWith("PRETTY_NAME=\"Parrot Security 7.3\"\nNAME=\"Parrot Security OS\"\nID=parrot\nID_LIKE=debian\nHOME_URL=\"https://www.parrotsec.org/\"\n");
            Assert.AreEqual(LinuxDistro.Parrot, result);
        }

        [TestMethod]
        public void DetectAsync_ExactIds_ClassifyDirectly()
        {
            Assert.AreEqual(LinuxDistro.Ubuntu, DetectWith("ID=ubuntu\n").result);
            Assert.AreEqual(LinuxDistro.Debian, DetectWith("ID=debian\n").result);
            Assert.AreEqual(LinuxDistro.Fedora, DetectWith("ID=fedora\n").result);
            Assert.AreEqual(LinuxDistro.OpenSuse, DetectWith("ID=opensuse-tumbleweed\n").result);
            Assert.AreEqual(LinuxDistro.OpenSuse, DetectWith("ID=opensuse-leap\n").result);
            Assert.AreEqual(LinuxDistro.Parrot, DetectWith("ID=parrot\n").result);
        }

        [TestMethod]
        public void DetectAsync_IdLikeFallback_ClassifiesDerivatives()
        {
            // Linux Mint: ID=linuxmint (unclassified) + ID_LIKE=ubuntu
            Assert.AreEqual(LinuxDistro.Ubuntu, DetectWith("ID=linuxmint\nID_LIKE=ubuntu\n").result);
            // Kali: unclassified ID + ID_LIKE=debian
            Assert.AreEqual(LinuxDistro.Debian, DetectWith("ID=kali\nID_LIKE=debian\n").result);
        }

        [TestMethod]
        public void DetectAsync_QuotedValues_AreUnquoted()
        {
            Assert.AreEqual(LinuxDistro.Debian, DetectWith("ID=\"debian\"\n").result);
            Assert.AreEqual(LinuxDistro.Parrot, DetectWith("ID='parrot'\n").result);
        }

        [TestMethod]
        public void DetectAsync_UnknownDistroOrEmpty_ReturnsUnknown()
        {
            Assert.AreEqual(LinuxDistro.Unknown, DetectWith("ID=arch\n").result);
            Assert.AreEqual(LinuxDistro.Unknown, DetectWith("").result);
        }

        [TestMethod]
        public async Task DetectAsync_CommandThrows_ReturnsNullNotUnknown()
        {
            // Transport failures (ssh dropped, timeout) produce NO verdict —
            // null — which InstallLamcoRdpStep distinguishes from Unknown
            // (guest answered but is genuinely not a Lamco distro). A
            // swallowed transport error previously masqueraded as "guest
            // reports an unsupported distro".
            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.VmName).Returns("TestVM");
            shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new System.IO.IOException("ssh dropped"));

            var result = await DistroDetector.DetectAsync(shell.Object, CancellationToken.None);

            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task InstallLamcoStep_RuntimeDistroMismatch_AbortsBeforeCopy()
        {
            // The gallery hint is a HINT; the actual guest wins. An
            // rpm-family guest claiming Debian metadata must abort the
            // Lamco install before any package staging.
            var step = new InstallLamcoRdpStep();
            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.VmName).Returns("TestVM");
            shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync("ID=fedora\nID_LIKE=rhel\n");
            shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync("LAMCO_RESULT=ok");
            var logger = new Mock<ILogger<InstallLamcoRdpStep>>();
            var item = new GalleryItem { LinuxDistro = LinuxDistro.Debian, InitialUsername = "user" };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                step.ExecuteAsync(shell.Object, item, new VmCustomizations { RdpBackend = RdpBackend.Lamco }, logger.Object, CancellationToken.None));

            shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
                "a runtime distro mismatch must abort before the script is copied");
        }

        [TestMethod]
        public async Task InstallLamcoStep_TransportFailureOnFirstRead_RetriesDetectionAndProceeds()
        {
            // A first-read ssh hiccup is NOT a distro verdict: the step
            // retries once, and a successful second read lets the install
            // continue rather than misreporting a supported guest as
            // unsupported.
            var step = new InstallLamcoRdpStep();
            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.VmName).Returns("TestVM");
            shell.SetupSequence(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new System.IO.IOException("ssh dropped"))
                 .ReturnsAsync("ID=parrot\n");
            shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync("LAMCO_RESULT=ok");
            var logger = new Mock<ILogger<InstallLamcoRdpStep>>();
            var item = new GalleryItem { LinuxDistro = LinuxDistro.Parrot, InitialUsername = "user" };

            await step.ExecuteAsync(shell.Object, item, new VmCustomizations { RdpBackend = RdpBackend.Lamco }, logger.Object, CancellationToken.None);

            shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
                "detection transport error was retried and answered — install should proceed");
        }

        [TestMethod]
        public async Task InstallLamcoStep_PersistentTransportFailure_ReportsTransportNotDistro()
        {
            // Two failed reads mean the guest was never actually queried —
            // the error must say "transport failure", not claim the guest
            // reported an incompatible distro.
            var step = new InstallLamcoRdpStep();
            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.VmName).Returns("TestVM");
            shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new System.IO.IOException("ssh dropped"));
            var logger = new Mock<ILogger<InstallLamcoRdpStep>>();
            var item = new GalleryItem { LinuxDistro = LinuxDistro.Parrot, InitialUsername = "user" };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                step.ExecuteAsync(shell.Object, item, new VmCustomizations { RdpBackend = RdpBackend.Lamco }, logger.Object, CancellationToken.None));

            StringAssert.Contains(ex.Message, "ssh transport failure, not a distro verdict");
            shell.Verify(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
                "an unreadable guest must abort before the script is copied");
        }
    }
}