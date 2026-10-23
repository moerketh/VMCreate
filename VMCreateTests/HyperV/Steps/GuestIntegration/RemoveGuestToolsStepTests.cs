using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public sealed class RemoveGuestToolsStepTests
    {
        private RemoveGuestToolsStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<RemoveGuestToolsStep>> _logger = null!;
        private GalleryItem _item = null!;
        private VmCustomizations _customizations = null!;

        [TestInitialize]
        public void Setup()
        {
            _step = new RemoveGuestToolsStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<RemoveGuestToolsStep>>();
            _item = new GalleryItem();
            _customizations = new VmCustomizations();
        }

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Remove VirtualBox/VMware Tools", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(50, _step.Order);
            Assert.AreEqual("Sub_RemoveGuestTools", _step.ProgressPhaseId);
            Assert.IsTrue(_step.IsApplicable(_item, _customizations));
        }

        [TestMethod]
        public void IsApplicable_AlwaysReturnsTrue()
        {
            Assert.IsTrue(_step.IsApplicable(null, null));
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations()));
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysAndRunsScript()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("done");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("#!/bin/bash") &&
                    content.Contains("vboxguest") &&
                    content.Contains("blacklist-vmware.conf") &&
                    content.Contains("open-vm-tools")),
                "/tmp/remove_guest_tools.sh",
                It.IsAny<CancellationToken>()), Times.Once);

            _shell.Verify(s => s.RunCommandAsync(
                "sudo bash /tmp/remove_guest_tools.sh && sudo rm -f /tmp/remove_guest_tools.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptContainsAllDistroPackageManagers()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("apt-get") &&
                    content.Contains("dnf") &&
                    content.Contains("pacman") &&
                    content.Contains("zypper")),
                "/tmp/remove_guest_tools.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptContainsInitramfsRegeneration()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("update-initramfs") &&
                    content.Contains("dracut") &&
                    content.Contains("mkinitcpio") &&
                    content.Contains("mkinitrd")),
                "/tmp/remove_guest_tools.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptContainsGrubUpdate()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("update-grub") &&
                    content.Contains("grub2-mkconfig") &&
                    content.Contains("grub-mkconfig")),
                "/tmp/remove_guest_tools.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ScriptGatesBootRegenOnRemoval()
        {
            // The expensive initramfs/GRUB regen must be conditional:
            // a clean guest (removed=0) must skip it entirely.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("");

            await _step.ExecuteAsync(_shell.Object, _item, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(content =>
                    content.Contains("if [ \"$removed\" -eq 1 ]") &&
                    content.Contains("update-initramfs")),
                "/tmp/remove_guest_tools.sh",
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
