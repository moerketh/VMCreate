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
        private Mock<IGuestShell> _shellMock = null!;
        private Mock<ILogger> _loggerMock = null!;
        private DeployVpnConfigsStep _step = null!;
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

            // VPN configs embed client certs + private keys: the copy MUST go
            // through CopySecretAsync (root:root 0600 on the guest), never the
            // world-readable 644 CopyContentAsync path.
            _shellMock.Verify(s => s.CopySecretAsync(
                "client config",
                "/etc/openvpn/client/lab.ovpn",
                It.IsAny<CancellationToken>()), Times.Once);
            _shellMock.Verify(s => s.CopyContentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
                "VPN key material must not be copied with the 644 contract");

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

                // Manual .ovpn embeds keys too: secret copy, not the 644 file path.
                _shellMock.Verify(s => s.CopySecretAsync(
                    "manual config",
                    "/etc/openvpn/client/manual.ovpn",
                    It.IsAny<CancellationToken>()), Times.Once);
                _shellMock.Verify(s => s.CopyFileAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
                    "manual .ovpn must not land with the 644 contract");

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

        private static HtbVpnDownloadResult Downloaded(string product, string name, string file) => new()
        {
            EndpointName = name,
            Product = product,
            Success = true,
            Key = new HtbVpnKey { Name = name, GuestFileName = file, OvpnContent = $"{name} config" },
        };

        private static Mock<IUserPreferencesStore> StoreWithToken(string? token)
        {
            var store = new Mock<IUserPreferencesStore>();
            store.Setup(s => s.Load()).Returns(new UserPreferences { HtbApiToken = token });
            return store;
        }

        [TestMethod]
        public async Task ExecuteAsync_NoPreloadedKeys_DownloadsWithStoredToken_AndFiltersProducts()
        {
            var api = new Mock<IHtbApiClient>();
            api.Setup(a => a.DownloadAllKeysAsync("stored-token", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new List<HtbVpnDownloadResult>
               {
                   Downloaded("labs", "Labs EU VIP 7", "labs_eu_vip_7.ovpn"),
                   Downloaded("fortresses", "Fortresses EU Fortress 1", "fortress_eu_fortress_1.ovpn"),
               });
            _shellMock.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync("");
            var step = new DeployVpnConfigsStep(api.Object, StoreWithToken("stored-token").Object);

            await step.ExecuteAsync(_shellMock.Object, new GalleryItem(),
                new VmCustomizations { ConfigureHtbVpn = true, HtbVpnProducts = "labs" },
                _loggerMock.Object, CancellationToken.None);

            _shellMock.Verify(s => s.CopySecretAsync(
                "Labs EU VIP 7 config", "/etc/openvpn/client/labs_eu_vip_7.ovpn", It.IsAny<CancellationToken>()), Times.Once);
            _shellMock.Verify(s => s.CopySecretAsync(
                It.IsAny<string>(), "/etc/openvpn/client/fortress_eu_fortress_1.ovpn", It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_NoStoredToken_DoesNotCallHtb()
        {
            var api = new Mock<IHtbApiClient>();
            _shellMock.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync("");
            var step = new DeployVpnConfigsStep(api.Object, StoreWithToken(null).Object);

            await step.ExecuteAsync(_shellMock.Object, new GalleryItem(),
                new VmCustomizations { ConfigureHtbVpn = true },
                _loggerMock.Object, CancellationToken.None);

            api.Verify(a => a.DownloadAllKeysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _shellMock.Verify(s => s.CopySecretAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_DownloadThrows_ContinuesWithoutFailingDeployment()
        {
            var api = new Mock<IHtbApiClient>();
            api.Setup(a => a.DownloadAllKeysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new System.Net.Http.HttpRequestException("offline"));
            _shellMock.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync("");
            var step = new DeployVpnConfigsStep(api.Object, StoreWithToken("t").Object);

            await step.ExecuteAsync(_shellMock.Object, new GalleryItem(),
                new VmCustomizations { ConfigureHtbVpn = true },
                _loggerMock.Object, CancellationToken.None);

            _shellMock.Verify(s => s.CopySecretAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_PreloadedKeys_SkipsDownload()
        {
            var api = new Mock<IHtbApiClient>();
            _shellMock.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync("");
            var step = new DeployVpnConfigsStep(api.Object, StoreWithToken("t").Object);

            await step.ExecuteAsync(_shellMock.Object, new GalleryItem(),
                new VmCustomizations
                {
                    ConfigureHtbVpn = true,
                    HtbVpnKeys = new List<HtbVpnKey> { new() { Name = "Lab", GuestFileName = "lab.ovpn", OvpnContent = "c" } },
                },
                _loggerMock.Object, CancellationToken.None);

            api.Verify(a => a.DownloadAllKeysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
