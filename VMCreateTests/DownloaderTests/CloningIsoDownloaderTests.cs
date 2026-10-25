using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Tests
{
    /// <summary>
    /// Downloader tests against a signed-release world. Each test creates
    /// its own signing key and passes it through the internal constructor —
    /// no test key ships, and production code always verifies with
    /// <see cref="IsoReleaseTrust.BuiltIn"/>.
    /// </summary>
    [TestClass]
    public class CloningIsoDownloaderTests
    {
        // Fixture fields — assigned in [TestInitialize] Setup() before every test.
        private string _tempDir = null!;
        private string _isoPath = null!;
        private string _manifestPath = null!;
        private string _signaturePath = null!;
        private VmDeploymentPlan _plan = null!;
        private Mock<IDownloader> _downloader = null!;
        private Mock<IHttpClientFactory> _clientFactory = null!;
        private ECDsa _signingKey = null!;
        private IsoReleaseTrust _trust = null!;

        // Per-test knobs controlling the stubbed GitHub API response and the
        // bytes the downloader mock "downloads".
        private string _latestTag = "v2.1.0";
        private bool _githubFails;
        private string _downloadContent = "downloaded iso bytes";
        private byte[] _manifestOverride = null!;
        private byte[] _signatureOverride = null!;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "vmcreate-isodl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _isoPath = Path.Combine(_tempDir, "vmcreate-iso.iso");
            _manifestPath = CloningIsoDownloader.GetManifestPath(_isoPath);
            _signaturePath = CloningIsoDownloader.GetSignaturePath(_isoPath);
            _plan = new VmDeploymentPlan("TestVM", cloningIsoPath: _isoPath);

            _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _trust = new IsoReleaseTrust(_signingKey.ExportSubjectPublicKeyInfoPem());

            _downloader = new Mock<IDownloader>();
            _downloader
                .Setup(d => d.DownloadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(),
                    It.IsAny<IProgress<CreateVMProgressInfo>>(), false))
                .ReturnsAsync(CreateDownloadedIso);

            _clientFactory = new Mock<IHttpClientFactory>();
#pragma warning disable CS8604 // Moq's It.IsAny<string?> matches the null name production passes
            _clientFactory
                .Setup(f => f.CreateClient(It.IsAny<string?>()))
                .Returns(() => new HttpClient(new StubHttpHandler(RequestAsync)));
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _signingKey.Dispose();
        }

        /// <summary>Serves the releases list, the manifest, and the
        /// signature from the stubbed GitHub API.</summary>
        private Task<HttpResponseMessage> RequestAsync(HttpRequestMessage request)
        {
            if (_githubFails)
                throw new HttpRequestException("simulated GitHub outage");

            string url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/releases?"))
            {
                string json = "[" + ReleaseJson(tag: _latestTag, signed: true)
                    + ",\n" + ReleaseJson(tag: "v2.0.0", signed: false) + "]";
                return Task.FromResult(Ok(json));
            }
            if (url.EndsWith(".manifest.json.sig"))
                return Task.FromResult(Ok(_signatureOverride?.Length > 0 ? _signatureOverride : SignatureFor(ManifestBytes())));
            if (url.EndsWith(".manifest.json"))
                return Task.FromResult(Ok(_manifestOverride?.Length > 0 ? _manifestOverride : ManifestBytes()));
            throw new InvalidOperationException($"stub: unexpected URL {url}");
        }

        private static HttpResponseMessage Ok(byte[] content) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };

        private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        };

        #region Manifest helpers

        private byte[] ManifestBytes(string? version = null, string? tag = null, string? isoFile = null,
                                     long? isoSize = null, string? isoSha256 = null)
        {
            version ??= _latestTag.TrimStart('v');
            tag ??= _latestTag;
            isoFile ??= $"vmcreate-{version}.iso";
            long size = isoSize ?? (long)_downloadContent.Length;
            string sha = isoSha256 ?? Sha256Of(_downloadContent);
            return Encoding.UTF8.GetBytes($$"""
                {
                  "schema": 1,
                  "name": "vmcreate-iso",
                  "version": "{{version}}",
                  "tag": "{{tag}}",
                  "commit": "62484c1a14fe7e0133c7e8da5924419e3476a1de",
                  "iso_file": "{{isoFile}}",
                  "iso_size": {{size}},
                  "iso_sha256": "{{sha}}",
                  "key_id": "{{IsoReleaseTrust.ComputeKeyId(_signingKey)}}"
                }
                """);
        }

        private byte[] SignatureFor(byte[] manifest) =>
            _signingKey.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        private static string Sha256Of(string content) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

        /// <summary>Builds one GitHub releases-list entry. Signed releases
        /// carry the manifest assets; unsigned ones only the .sha256 (the
        /// v2.0.0 shape).</summary>
        private static string ReleaseJson(string tag, bool signed)
        {
            string version = tag.TrimStart('v');
            string assets = signed
                ? $$"""
                [
                      { "name": "vmcreate-{{version}}.iso", "browser_download_url": "https://github.com/moerketh/vmcreate-iso/releases/download/{{tag}}/vmcreate-{{version}}.iso" },
                      { "name": "vmcreate-{{version}}.manifest.json", "browser_download_url": "https://github.com/moerketh/vmcreate-iso/releases/download/{{tag}}/vmcreate-{{version}}.manifest.json" },
                      { "name": "vmcreate-{{version}}.manifest.json.sig", "browser_download_url": "https://github.com/moerketh/vmcreate-iso/releases/download/{{tag}}/vmcreate-{{version}}.manifest.json.sig" }
                    ]
                """
                : $$"""
                [
                      { "name": "vmcreate-{{version}}.iso", "browser_download_url": "https://github.com/moerketh/vmcreate-iso/releases/download/{{tag}}/vmcreate-{{version}}.iso" },
                      { "name": "vmcreate-{{version}}.iso.sha256", "browser_download_url": "https://github.com/moerketh/vmcreate-iso/releases/download/{{tag}}/vmcreate-{{version}}.iso.sha256" }
                    ]
                """;

            return $$"""
                {
                    "tag_name": "{{tag}}",
                    "prerelease": false,
                    "published_at": "2026-01-01T00:00:00Z",
                    "assets": {{assets}}
                  }
                """;
        }

        #endregion

        #region Downloading a signed release

        [TestMethod]
        public async Task EnsureIsoAsync_MissingIso_DownloadsSignedReleaseAndCachesManifest()
        {
            var downloader = CreateDownloader();

            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            Assert.AreEqual("downloaded iso bytes", File.ReadAllText(_isoPath));
            Assert.IsTrue(File.Exists(_manifestPath), "the manifest must be cached next to the ISO");
            Assert.IsTrue(File.Exists(_signaturePath), "the signature must be cached next to the ISO");
            Assert.AreEqual(IsoReleaseTrust.ComputeKeyId(_signingKey), CachedKeyId());
            VerifyDownloaded(Times.Once());
        }

        [TestMethod]
        public async Task EnsureIsoAsync_SkipsUnsignedReleases_PicksNewestSigned()
        {
            // The list contains v2.0.0 (unsigned, published later) and the
            // signed v2.1.0. The downloader must skip v2.0.0 and take v2.1.0.
            var downloader = CreateDownloader();

            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            Assert.AreEqual("downloaded iso bytes", File.ReadAllText(_isoPath));
            Assert.AreEqual("2.1.0", CachedManifest().Version);
        }

        [TestMethod]
        public async Task EnsureIsoAsync_NoSignedRelease_FailsClosed()
        {
            // Every release is unsigned (v2.0.0 shape) — there is nothing
            // VMCreate could verify before boot.
            _clientFactory.Setup(f => f.CreateClient(It.IsAny<string?>()))
                .Returns(() => new HttpClient(new StubHttpHandler(request =>
                {
                    if (_githubFails)
                        throw new HttpRequestException("simulated GitHub outage");
                    string json = "[" + ReleaseJson(_latestTag, signed: false) + "]";
                    return Task.FromResult(Ok(json));
                })));
#pragma warning restore CS8604
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>()));

            Assert.IsFalse(File.Exists(_isoPath), "an unverified ISO must not be installed into the cache");
            Assert.IsFalse(File.Exists(_manifestPath), "no manifest may be cached for a refused download");
        }

        [TestMethod]
        public async Task EnsureIsoAsync_TamperedManifestSignature_Fails()
        {
            // The signature served does not cover the served manifest.
            _signatureOverride = SignatureFor(Encoding.UTF8.GetBytes("something else"));
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>()));

            Assert.IsFalse(File.Exists(_isoPath), "a release whose manifest signature fails must not be installed");
        }

        [TestMethod]
        public async Task EnsureIsoAsync_UnknownKeyId_Fails()
        {
            // The manifest names a key VMCreate does not trust: the trust
            // store holds only the test signing key, and the manifest
            // claims a different id.
            byte[] manifest = ManifestBytes();
            byte[] swapped = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(manifest)
                    .Replace(IsoReleaseTrust.ComputeKeyId(_signingKey), "0011223344556677"));
            _manifestOverride = swapped;
            _signatureOverride = SignatureFor(swapped); // genuinely signed over the swapped bytes
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>()));

            Assert.IsFalse(File.Exists(_isoPath));
        }

        [TestMethod]
        public async Task EnsureIsoAsync_ManifestVersionMismatch_Fails()
        {
            // Signed, but describes a different release than the one it is
            // served from.
            _manifestOverride = ManifestBytes(version: "9.9.9", tag: "v9.9.9",
                isoFile: "vmcreate-9.9.9.iso", isoSize: (long)_downloadContent.Length,
                isoSha256: Sha256Of(_downloadContent));
            _signatureOverride = SignatureFor(_manifestOverride);
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>()));

            Assert.IsFalse(File.Exists(_isoPath));
        }

        [TestMethod]
        public async Task EnsureIsoAsync_ManifestIsoFileNameMismatch_Fails()
        {
            // Signed, but names a different ISO than the release ships.
            _manifestOverride = ManifestBytes(isoFile: "vmcreate-other.iso");
            _signatureOverride = SignatureFor(_manifestOverride);
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>()));

            Assert.IsFalse(File.Exists(_isoPath));
        }

        [TestMethod]
        public async Task EnsureIsoAsync_IsoDoesNotMatchManifest_Fails()
        {
            // The manifest is signed over the release's ISO, but the bytes
            // that arrive do not hash to the signed value.
            _manifestOverride = ManifestBytes(version: null, tag: null, isoFile: null,
                isoSize: (long)"downloaded iso bytes".Length, isoSha256: Sha256Of("downloaded iso bytes"));
            _signatureOverride = SignatureFor(_manifestOverride);
            _downloadContent = "tampered iso bytes";
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>()));

            Assert.IsFalse(File.Exists(_isoPath), "an ISO that does not match its signed manifest must not be cached");
        }

        [TestMethod]
        public async Task EnsureIsoAsync_UpToDateSignedCache_SkipsDownload()
        {
            await SeedCacheAsync();
            var downloader = CreateDownloader();

            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            VerifyDownloaded(Times.Never());
            Assert.AreEqual("downloaded iso bytes", File.ReadAllText(_isoPath), "the cached ISO must be kept");
        }

        [TestMethod]
        public async Task EnsureIsoAsync_NewerSignedRelease_DownloadsUpdate()
        {
            await SeedCacheAsync();
            _latestTag = "v2.2.0";
            _downloadContent = "updated iso bytes";
            var downloader = CreateDownloader();

            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            Assert.AreEqual("updated iso bytes", File.ReadAllText(_isoPath));
            Assert.AreEqual("2.2.0", CachedManifest().Version);
            VerifyDownloaded(Times.Once());
        }

        [TestMethod]
        public async Task EnsureIsoAsync_OlderOnlineRelease_DoesNotReplaceVerifiedCache()
        {
            // Point 6: GitHub's newest signed release is OLDER than the
            // verified cache (e.g. the newer release was deleted). The
            // cache must win.
            await SeedCacheAsync();
            _latestTag = "v2.0.5";
            var downloader = CreateDownloader();

            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            Assert.AreEqual("downloaded iso bytes", File.ReadAllText(_isoPath), "the newer cached ISO must be kept");
            Assert.AreEqual("2.1.0", CachedManifest().Version);
            VerifyDownloaded(Times.Never());
        }

        [TestMethod]
        public async Task EnsureIsoAsync_LegacyVersionOnlyCache_RedownloadsOnce()
        {
            // An old VMCreate wrote only .version next to the ISO. That
            // cache is unverified: it must be replaced with a signed
            // release.
            File.WriteAllText(_isoPath, "stale legacy iso");
            File.WriteAllText(_isoPath + ".version", "2.1.0");
            var downloader = CreateDownloader();

            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            Assert.AreEqual("downloaded iso bytes", File.ReadAllText(_isoPath));
            Assert.IsTrue(File.Exists(_manifestPath), "the signed manifest replaces the .version sidecar");
            Assert.IsFalse(File.Exists(_isoPath + ".version"), "the legacy .version file must be gone");
            VerifyDownloaded(Times.Once());
        }

        #endregion

        #region Offline behavior

        [TestMethod]
        public async Task EnsureIsoAsync_GitHubUnreachableWithVerifiedCache_KeepsCache()
        {
            await SeedCacheAsync();
            _githubFails = true;
            var downloader = CreateDownloader();

            // Must not throw: the cache verifies, so it is usable offline.
            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());

            Assert.AreEqual("downloaded iso bytes", File.ReadAllText(_isoPath));
            VerifyDownloaded(Times.Never());
        }

        [TestMethod]
        public async Task EnsureIsoAsync_GitHubUnreachableWithUnverifiedCache_Fails()
        {
            // The cache exists but its manifest does not verify — offline
            // or not, an unverified ISO never boots.
            File.WriteAllText(_isoPath, "tampered iso bytes");
            File.WriteAllBytes(_manifestPath, Encoding.UTF8.GetBytes("{}"));
            File.WriteAllBytes(_signaturePath, new byte[72]);
            _githubFails = true;
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>()));

            Assert.AreEqual("tampered iso bytes", File.ReadAllText(_isoPath),
                "the unverified cache is left alone — the deploy just refuses to use it");
        }

        [TestMethod]
        public async Task EnsureIsoAsync_GitHubUnreachableWithoutIso_Throws()
        {
            _githubFails = true;
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<Exception>(() =>
                downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>()));

            VerifyDownloaded(Times.Never());
        }

        #endregion

        #region Pre-boot verification

        [TestMethod]
        public async Task VerifyCachedIsoAsync_ValidCache_Passes()
        {
            await SeedCacheAsync();
            var downloader = CreateDownloader();

            await downloader.VerifyCachedIsoAsync(_plan, CancellationToken.None);
        }

        [TestMethod]
        public async Task VerifyCachedIsoAsync_TamperedIso_Throws()
        {
            await SeedCacheAsync();
            File.WriteAllText(_isoPath, "tampered iso bytes");
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                downloader.VerifyCachedIsoAsync(_plan, CancellationToken.None));
        }

        [TestMethod]
        public async Task VerifyCachedIsoAsync_TamperedManifest_Throws()
        {
            await SeedCacheAsync();
            byte[] manifest = CachedManifest().ManifestBytes;
            byte[] tampered = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(manifest).Replace("\"2.1.0\"", "\"9.9.9\""));
            File.WriteAllBytes(_manifestPath, tampered);
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                downloader.VerifyCachedIsoAsync(_plan, CancellationToken.None));
        }

        [TestMethod]
        public async Task VerifyCachedIsoAsync_MissingCache_Throws()
        {
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                downloader.VerifyCachedIsoAsync(_plan, CancellationToken.None));
        }

        [TestMethod]
        public async Task VerifyCachedIsoGuardedAsync_HoldsReadHandleUntilDisposed()
        {
            await SeedCacheAsync();
            var downloader = CreateDownloader();

            using (IIsoReadGuard guard = await downloader.VerifyCachedIsoGuardedAsync(_plan, CancellationToken.None))
            {
                Assert.Throws<IOException>(() =>
                    File.Open(_isoPath, FileMode.Open, FileAccess.Write, FileShare.Read),
                    "while the guard is held, no writer can replace the ISO");
            }

            // Released: writers can open the file again.
            using (FileStream writer = File.Open(_isoPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
            }
        }

        [TestMethod]
        public async Task VerifyCachedIsoAsync_UnsignedCache_Throws()
        {
            // The .version-only legacy cache is not a verified cache.
            File.WriteAllText(_isoPath, "stale legacy iso");
            File.WriteAllText(_isoPath + ".version", "2.1.0");
            var downloader = CreateDownloader();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                downloader.VerifyCachedIsoAsync(_plan, CancellationToken.None));
        }

        #endregion

        #region Test Helpers

        /// <summary>Runs a full download against the stubbed release and
        /// leaves the verified cache in place. The download counter is
        /// reset afterwards so tests verify only their own
        /// downloads.</summary>
        private async Task SeedCacheAsync()
        {
            var downloader = CreateDownloader();
            await downloader.EnsureIsoAsync(_plan, CancellationToken.None, new Progress<CreateVMProgressInfo>());
            _downloader.Invocations.Clear();
        }

        private CloningIsoDownloader CreateDownloader() => new CloningIsoDownloader(
            _downloader.Object,
            _clientFactory.Object,
            new Mock<ILogger<CloningIsoDownloader>>().Object,
            _trust);

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

        private VerifiedIsoManifest CachedManifest()
        {
            byte[] manifestBytes = File.ReadAllBytes(_manifestPath);
            byte[] signatureBytes = File.ReadAllBytes(_signaturePath);
            if (_trust.VerifyManifestSignature(manifestBytes, signatureBytes) == null)
                Assert.Fail("test fixture: cached manifest does not verify with the test key");
            return ParseManifest(manifestBytes, signatureBytes);
        }

        private string CachedKeyId()
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(_manifestPath));
            return doc.RootElement.GetProperty("key_id").GetString()!;
        }

        private static VerifiedIsoManifest ParseManifest(byte[] manifestBytes, byte[] signatureBytes)
        {
            using JsonDocument doc = JsonDocument.Parse(manifestBytes);
            JsonElement root = doc.RootElement;
            return new VerifiedIsoManifest
            {
                ManifestBytes = manifestBytes,
                SignatureBytes = signatureBytes,
                Version = root.GetProperty("version").GetString()!,
                Tag = root.GetProperty("tag").GetString()!,
                Name = root.GetProperty("name").GetString()!,
                Schema = root.GetProperty("schema").GetInt64(),
                IsoFile = root.GetProperty("iso_file").GetString()!,
                IsoSize = root.GetProperty("iso_size").GetInt64(),
                IsoSha256 = root.GetProperty("iso_sha256").GetString()!,
                KeyId = root.GetProperty("key_id").GetString()!,
            };
        }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responseFactory;

            public StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
                => _responseFactory = responseFactory;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => _responseFactory(request);
        }

        #endregion
    }
}