using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    [TestClass]
    public class DeployVpnConfigsStepTests
    {
        private Mock<IGuestShell> _shellMock;
        private Mock<ILogger> _loggerMock;
        private DeployVpnConfigsStep _step;

        [TestInitialize]
        public void Setup()
        {
            _shellMock = new Mock<IGuestShell>();
            _loggerMock = new Mock<ILogger>();
            _step = new DeployVpnConfigsStep();
        }

        [TestMethod]
        public void Metadata_IsCorrect()
        {
            Assert.AreEqual("Deploy VPN Configs", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(300, _step.Order);
            Assert.AreEqual("Sub_ConfigureVpn", _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_WhenConfigureHtbVpnTrue_ReturnsTrue()
        {
            Assert.IsTrue(_step.IsApplicable(new GalleryItem(), new VmCustomizations { ConfigureHtbVpn = true }));
        }

        [TestMethod]
        public void IsApplicable_WhenConfigureHtbVpnFalse_ReturnsFalse()
        {
            Assert.IsFalse(_step.IsApplicable(new GalleryItem(), new VmCustomizations { ConfigureHtbVpn = false }));
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysHtbKeys_AndImportsIntoNm()
        {
            _shellMock
                .Setup(s => s.RunCommandAsync(
                    It.Is<string>(cmd => cmd.Contains("nmcli connection import")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("Connection successfully imported");

            _shellMock
                .Setup(s => s.RunCommandAsync(
                    It.Is<string>(cmd => cmd.Contains("nmcli connection modify")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("");

            _shellMock
                .Setup(s => s.RunCommandAsync(
                    "nmcli connection show 2>&1 | grep -i vpn || echo 'no-vpn-connections'",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("HTB Lab");

            var customizations = new VmCustomizations
            {
                ConfigureHtbVpn = true,
                HtbVpnKeys = new List<HtbVpnKey>
                {
                    new HtbVpnKey
                    {
                        Name = "Lab",
                        GuestFileName = "lab.ovpn",
                        OvpnContent = "client config"
                    }
                }
            };

            await _step.ExecuteAsync(
                _shellMock.Object,
                new GalleryItem(),
                customizations,
                _loggerMock.Object,
                CancellationToken.None);

            _shellMock.Verify(s => s.CopyContentAsync(
                "client config",
                "/etc/openvpn/client/lab.ovpn",
                It.IsAny<CancellationToken>()), Times.Once);

            _shellMock.Verify(s => s.RunCommandAsync(
                "sudo nmcli connection import type openvpn file '/etc/openvpn/client/lab.ovpn' 2>&1",
                It.IsAny<CancellationToken>()), Times.Once);

            _shellMock.Verify(s => s.RunCommandAsync(
                "sudo nmcli connection modify 'lab' connection.id 'HTB Lab' 2>&1",
                It.IsAny<CancellationToken>()), Times.Once);

            _shellMock.Verify(s => s.RunCommandAsync(
                "sudo nmcli connection modify 'HTB Lab' ipv4.never-default yes ipv6.never-default yes 2>&1",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_DeploysManualOvpnFile_WhenPathProvided()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".ovpn");
            File.WriteAllText(tempFile, "manual config");

            try
            {
                _shellMock
                    .Setup(s => s.RunCommandAsync(
                        It.Is<string>(cmd => cmd.Contains("nmcli connection import")),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync("Connection successfully imported");

                _shellMock
                    .Setup(s => s.RunCommandAsync(
                        "sudo nmcli connection modify 'manual' connection.id 'HTB Manual' 2>&1",
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync("");

                _shellMock
                    .Setup(s => s.RunCommandAsync(
                        "nmcli connection show 2>&1 | grep -i vpn || echo 'no-vpn-connections'",
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync("HTB Manual");

                var customizations = new VmCustomizations
                {
                    ConfigureHtbVpn = true,
                    OvpnFilePath = tempFile
                };

                await _step.ExecuteAsync(
                    _shellMock.Object,
                    new GalleryItem(),
                    customizations,
                    _loggerMock.Object,
                    CancellationToken.None);

                _shellMock.Verify(s => s.CopyFileAsync(
                    tempFile,
                    "/etc/openvpn/client/manual.ovpn",
                    It.IsAny<CancellationToken>()), Times.Once);

                _shellMock.Verify(s => s.RunCommandAsync(
                    "sudo nmcli connection import type openvpn file '/etc/openvpn/client/manual.ovpn' 2>&1",
                    It.IsAny<CancellationToken>()), Times.Once);

                _shellMock.Verify(s => s.RunCommandAsync(
                    "sudo nmcli connection modify 'manual' connection.id 'HTB Manual' 2>&1",
                    It.IsAny<CancellationToken>()), Times.Once);

                _shellMock.Verify(s => s.RunCommandAsync(
                    "sudo nmcli connection modify 'HTB Manual' ipv4.never-default yes ipv6.never-default yes 2>&1",
                    It.IsAny<CancellationToken>()), Times.Once);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }
}
