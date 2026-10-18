using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Tests
{
    [TestClass]
    [DoNotParallelize] // sets VMCREATE_ISO_PATH; plan construction resolves it
    public class CloningIsoDownloaderTests
    {
        // Fixture fields — assigned in [TestInitialize] Setup() before every test.
        private string _tempDir = null!;
        private string _isoPath = null!;
        private string _versionPath = null!;
        private VmDeploymentPlan _plan = null!;
        private Mock<IDownloader> _downloader = null!;
        private Mock<IChecksumVerifier> _checksumVerifier = null!;
        private Mock<IHttpClientFactory> _clientFactory = null!;

        // Per-test knobs controlling the stubbed GitHub API response and the
        // bytes the downloader mock "downloads".
        private string _latestTag = "v1.1.4";
        private bool _githubFails;
        private string _downloadContent = "downloaded iso bytes";

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "vmcreate-isodl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _isoPath = Path.Combine(_tempDir, "hyperv-convert.iso");
            _versionPath = _isoPath + ".version";
            _plan = new VmDeploymentPlan("TestVM", cloningIsoPath: _isoPath);

            _downloader = new Mock<IDownloader>();
            _downloader
                .Setup(d => d.DownloadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(),
                    It.IsAny<IProgress<CreateVMProgressInfo>>(), false))
                .ReturnsAsync(() => CreateDownloadedIso());

            _checksumVerifier = new Mock<IChecksumVerifier>();

            var handler = new StubHttpHandler(() =>
            {
                if (_githubFails)
                    throw new HttpRequestException("simulated GitHub outage");

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ReleaseJson(_latestTag))
                };
            });

            _clientFactory = new Mock<IHttpClientFactory>();
            _clientFactory
                .Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(handler));
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        [TestMethod]
        public async Task EnsureIsoAsync_MissingIso_DownloadsLatestAndWritesVersionFile()
        {
            // Arrange: no cached ISO, GitHub reports v1.1.4.
            var downloader = CreateDownloader();

            // Act
            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            // Assert
            Assert.IsTrue(File.Exists(_isoPath));
            Assert.AreEqual("downloaded iso bytes", File.ReadAllText(_isoPath));
            Assert.AreEqual("1.1.4", File.ReadAllText(_versionPath),
                "The version file must hold the release tag without its v prefix.");
            VerifyDownloaded(Times.Once());
            _checksumVerifier.Verify(v => v.VerifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), "sha256",
                It.IsAny<CancellationToken>(), It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<string?>()), Times.Once());
        }

        [TestMethod]
        public async Task EnsureIsoAsync_CachedVersionMatchesLatest_SkipsDownload()
        {
            // Arrange: a cached ISO whose recorded version equals the live tag.
            // The version file carries a stray newline to prove it is trimmed.
            File.WriteAllText(_isoPath, "cached iso bytes");
            File.WriteAllText(_versionPath, "1.1.4\n");
            var downloader = CreateDownloader();

            // Act
            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            Assert.AreEqual("cached iso bytes", File.ReadAllText(_isoPath));
            VerifyDownloaded(Times.Never());
        }

        [TestMethod]
        public async Task EnsureIsoAsync_IsoWithoutVersionFile_Redownloads()
        {
            // Arrange: an ISO cached by an older VMCreate write-up — no sidecar
            // version file, so its age cannot be trusted.
            File.WriteAllText(_isoPath, "stale pre-guard iso");
            var downloader = CreateDownloader();

            // Act
            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            // Assert
            Assert.AreEqual("downloaded iso bytes", File.ReadAllText(_isoPath));
            Assert.AreEqual("1.1.4", File.ReadAllText(_versionPath));
            VerifyDownloaded(Times.Once());
        }

        [TestMethod]
        public async Task EnsureIsoAsync_EnvOverride_UsesIsoAsIs_NoNetworkNoOverwrite()
        {
            // A locally built ISO behind VMCREATE_ISO_PATH is NOT the
            // downloader's cache: no sidecar exists (would read as
            // "outdated"), and the download path would either fail the
            // deploy (no release published) or REPLACE the dev's build with
            // the published one. The override must short-circuit before any
            // of that.
            string localIso = Path.Combine(_tempDir, "local-build.iso");
            const string localBytes = "locally built iso bytes";
            File.WriteAllText(localIso, localBytes);
            Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", localIso);
            try
            {
                var overriddenPlan = new VmDeploymentPlan("TestVM", cloningIsoPath: _isoPath);
                Assert.IsTrue(overriddenPlan.IsIsoPathOverridden, "fixture sanity: override must be active");
                var downloader = CreateDownloader();

                await downloader.EnsureIsoAsync(overriddenPlan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

                Assert.AreEqual(localBytes, File.ReadAllText(localIso), "the local ISO must be untouched");
                Assert.IsFalse(File.Exists(localIso + ".version"), "no sidecar may be written next to it");
                VerifyDownloaded(Times.Never());
                _clientFactory.Verify(f => f.CreateClient(It.IsAny<string?>()), Times.Never,
                    "no GitHub API call may happen under the override");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", null);
            }
        }

        [TestMethod]
        public async Task EnsureIsoAsync_OutdatedCachedVersion_DownloadsUpdate()
        {
            // Arrange: cached 1.1.4, GitHub reports v1.1.5.
            File.WriteAllText(_isoPath, "old iso bytes");
            File.WriteAllText(_versionPath, "1.1.4");
            _latestTag = "v1.1.5";
            _downloadContent = "updated iso bytes";
            var downloader = CreateDownloader();

            // Act
            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            // Assert
            Assert.AreEqual("updated iso bytes", File.ReadAllText(_isoPath),
                "The outdated ISO must be replaced by the newer release.");
            Assert.AreEqual("1.1.5", File.ReadAllText(_versionPath));
            VerifyDownloaded(Times.Once());
        }

        [TestMethod]
        public async Task EnsureIsoAsync_GitHubUnreachableWithCachedVersion_KeepsCache()
        {
            // Arrange: the API fails all retries, but a versioned ISO is cached.
            File.WriteAllText(_isoPath, "cached iso bytes");
            File.WriteAllText(_versionPath, "1.1.4");
            _githubFails = true;
            var downloader = CreateDownloader();

            // Act — must not throw.
            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            // Assert
            Assert.IsTrue(File.Exists(_isoPath));
            Assert.AreEqual("cached iso bytes", File.ReadAllText(_isoPath));
            VerifyDownloaded(Times.Never());
        }

        [TestMethod]
        public async Task EnsureIsoAsync_GitHubUnreachableWithoutIso_Throws()
        {
            // Arrange: the API fails all retries and there is nothing cached
            // to fall back on.
            _githubFails = true;
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<Exception>(() =>
                downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>()));

            VerifyDownloaded(Times.Never());
        }

        #region Test Helpers

        private CloningIsoDownloader CreateDownloader() => new CloningIsoDownloader(
            _downloader.Object,
            _checksumVerifier.Object,
            _clientFactory.Object,
            new Mock<ILogger<CloningIsoDownloader>>().Object);

        private string CreateDownloadedIso()
        {
            string path = Path.Combine(_tempDir, $"download-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(path, _downloadContent);
            return path;
        }

        private void VerifyDownloaded(Times times) =>
            _downloader.Verify(d => d.DownloadFileAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(), false), times);

        private static string ReleaseJson(string tag)
        {
            string version = tag.TrimStart('v');
            return $$"""
                {
                  "tag_name": "{{tag}}",
                  "assets": [
                    {
                      "name": "hyperv-convert-{{version}}.iso",
                      "browser_download_url": "https://github.com/moerketh/hyperv-convert-iso/releases/download/{{tag}}/hyperv-convert-{{version}}.iso"
                    },
                    {
                      "name": "hyperv-convert-{{version}}.iso.sha256",
                      "browser_download_url": "https://github.com/moerketh/hyperv-convert-iso/releases/download/{{tag}}/hyperv-convert-{{version}}.iso.sha256"
                    }
                  ]
                }
                """;
        }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpResponseMessage> _responseFactory;

            public StubHttpHandler(Func<HttpResponseMessage> responseFactory) => _responseFactory = responseFactory;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_responseFactory());
        }

        #endregion
    }
}