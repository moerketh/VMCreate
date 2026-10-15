using System.Net;
using System.Net.Http;
using Moq;
using VMCreate.Gallery;
using VMCreate.Gallery.distributions;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Tests for statically-defined loaders that return hard-coded gallery items
    /// without any network I/O or external dependencies.
    /// </summary>
    [TestClass]
    public sealed class StaticLoaderTests
    {
        /// <summary>Returns a mock IHttpClientFactory whose client always responds with empty HTML.</summary>
        private static IHttpClientFactory EmptyFactory()
        {
            var mock = new Mock<IHttpClientFactory>();
            mock.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(new EmptyHandler(), disposeHandler: false));
            return mock.Object;
        }

        private sealed class EmptyHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html></html>")
                });
        }

        // ── openSUSE Tumbleweed (KDE) ─────────────────────────────────────────────

        [TestMethod]
        public async Task OpenSuseTumbleweed_LoadGalleryItems_ReturnsSingleItem()
        {
            var result = await new OpenSuseTumbleweed().LoadGalleryItems();

            Assert.AreEqual(1, result.Count);
        }

        [TestMethod]
        public async Task OpenSuseTumbleweed_LoadGalleryItems_ItemHasRequiredFields()
        {
            var item = (await new OpenSuseTumbleweed().LoadGalleryItems())[0];

            Assert.IsFalse(string.IsNullOrEmpty(item.Name));
            Assert.IsFalse(string.IsNullOrEmpty(item.DiskUri), "DiskUri must not be empty");
            Assert.AreEqual("HvSocket", item.EnhancedSessionTransportType);
            Assert.AreEqual("false", item.SecureBoot);
        }

        [TestMethod]
        public async Task OpenSuseTumbleweed_LoadGalleryItems_SetsOpenSuseDistroHint()
        {
            // The LinuxDistro hint drives pre-deployment UI gating. openSUSE
            // is classified but NOT Lamco-supported: the install path is a
            // pinned amd64 Debian deb and the fork pipeline ships no rpms.
            var item = (await new OpenSuseTumbleweed().LoadGalleryItems())[0];

            Assert.AreEqual(LinuxDistro.OpenSuse, item.LinuxDistro);
            Assert.IsFalse(item.SupportsLamco());
        }

        [TestMethod]
        public async Task OpenSuseTumbleweed_LoadGalleryItems_DiskUriIsIso()
        {
            var item = (await new OpenSuseTumbleweed().LoadGalleryItems())[0];

            Assert.IsTrue(item.DiskUri.EndsWith(".iso", StringComparison.OrdinalIgnoreCase),
                $"Expected .iso URI for the KDE Live spin, got: {item.DiskUri}");
        }

        // ── Arch ─────────────────────────────────────────────────────────────────

        [TestMethod]
        public async Task Arch_LoadGalleryItems_ReturnsSingleItem()
        {
            var result = await new Arch().LoadGalleryItems();

            Assert.AreEqual(1, result.Count);
        }

        [TestMethod]
        public async Task Arch_LoadGalleryItems_ItemHasRequiredFields()
        {
            var item = (await new Arch().LoadGalleryItems())[0];

            Assert.AreEqual("Arch", item.Name);
            Assert.IsFalse(string.IsNullOrEmpty(item.DiskUri), "DiskUri must not be empty");
            Assert.AreEqual("Arch Linux", item.Publisher);
            Assert.AreEqual("false", item.SecureBoot);
            Assert.AreEqual("HvSocket", item.EnhancedSessionTransportType);
        }

        [TestMethod]
        public async Task Arch_LoadGalleryItems_DiskUriIsQcow2()
        {
            var item = (await new Arch().LoadGalleryItems())[0];

            Assert.IsTrue(item.DiskUri.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase),
                $"Expected .qcow2 URI, got: {item.DiskUri}");
        }

        [TestMethod]
        public async Task Arch_LoadGalleryItems_HasMirrorSha256Sidecar()
        {
            var item = (await new Arch().LoadGalleryItems())[0];

            // The Arch mirror publishes a GNU-format .SHA256 file next to the
            // qcow2 template (uppercase extension — lowercase 404s on nginx).
            Assert.IsTrue(item.HasChecksum,
                "Arch must publish a checksum so the downloaded template can be verified");
            Assert.AreEqual(item.DiskUri + ".SHA256", item.ChecksumUri,
                $"ChecksumUri must be the mirror's .SHA256 sidecar beside the qcow2, got: {item.ChecksumUri}");
            Assert.IsTrue(string.IsNullOrEmpty(item.Checksum),
                "No inline hash expected — verification must ride the sidecar");
        }

        [TestMethod]
        public async Task Arch_LoadGalleryItems_CancellationTokenIgnored()
        {
            // Static loaders do not observe the token — calling with a cancelled token
            // must still return results immediately without throwing.
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = await new Arch().LoadGalleryItems(cts.Token);

            Assert.AreEqual(1, result.Count);
        }

        // ── NixOS ─────────────────────────────────────────────────────────────────

        [TestMethod]
        public async Task NixOS_LoadGalleryItems_ReturnsTwoItems()
        {
            var result = await new NixOS().LoadGalleryItems();

            Assert.AreEqual(2, result.Count,
                "NixOS should return a Graphical and a Minimal entry.");
        }

        [TestMethod]
        public async Task NixOS_LoadGalleryItems_GraphicalAndMinimalPresent()
        {
            var result = await new NixOS().LoadGalleryItems();
            var names = result.Select(i => i.Name).ToList();

            CollectionAssert.Contains(names, "NixOS Graphical");
            CollectionAssert.Contains(names, "NixOS Minimal");
        }

        [TestMethod]
        public async Task NixOS_LoadGalleryItems_AllItemsHavePublisher()
        {
            var result = await new NixOS().LoadGalleryItems();

            foreach (var item in result)
                Assert.AreEqual("NixOS Foundation", item.Publisher, $"Publisher wrong on '{item.Name}'");
        }

        [TestMethod]
        public async Task NixOS_LoadGalleryItems_DiskUrisAreIsos()
        {
            var result = await new NixOS().LoadGalleryItems();

            foreach (var item in result)
                Assert.IsTrue(item.DiskUri.EndsWith(".iso", StringComparison.OrdinalIgnoreCase),
                    $"Expected .iso URI for '{item.Name}', got: {item.DiskUri}");
        }

        // ── FedoraSilverblue ──────────────────────────────────────────────────────

        [TestMethod]
        public async Task FedoraSilverblue_LoadGalleryItems_ReturnsSingleItem()
        {
            var result = await new FedoraSilverblue().LoadGalleryItems();

            Assert.AreEqual(1, result.Count);
        }

        [TestMethod]
        public async Task FedoraSilverblue_LoadGalleryItems_ItemHasRequiredFields()
        {
            var item = (await new FedoraSilverblue().LoadGalleryItems())[0];

            Assert.AreEqual("Fedora Silverblue", item.Name);
            Assert.AreEqual("Fedora Project", item.Publisher);
            Assert.IsFalse(string.IsNullOrEmpty(item.DiskUri));
            Assert.IsFalse(string.IsNullOrEmpty(item.Version));
        }

        [TestMethod]
        public async Task FedoraSilverblue_LoadGalleryItems_DiskUriContainsFedora()
        {
            var item = (await new FedoraSilverblue().LoadGalleryItems())[0];

            StringAssert.Contains(item.DiskUri, "fedora", StringComparison.OrdinalIgnoreCase);
        }

        // ── PwnCloudOS ────────────────────────────────────────────────────────────

        [TestMethod]
        public async Task PwnCloudOS_LoadGalleryItems_ReturnsSingleItem()
        {
            var result = await new PwnCloudOS(EmptyFactory()).LoadGalleryItems();

            Assert.AreEqual(1, result.Count);
        }

        [TestMethod]
        public async Task PwnCloudOS_LoadGalleryItems_ItemHasNonEmptyNameAndUri()
        {
            var item = (await new PwnCloudOS(EmptyFactory()).LoadGalleryItems())[0];

            Assert.IsFalse(string.IsNullOrEmpty(item.Name));
            Assert.IsFalse(string.IsNullOrEmpty(item.DiskUri));
        }

        // ── LoadFedoraSecurityLab ─────────────────────────────────────────────────

        [TestMethod]
        public async Task LoadFedoraSecurityLab_LoadGalleryItems_ReturnsSingleItem()
        {
            var result = await new FedoraSecurityLab().LoadGalleryItems();

            Assert.AreEqual(1, result.Count);
        }

        [TestMethod]
        public async Task LoadFedoraSecurityLab_LoadGalleryItems_NameContainsFedora()
        {
            var item = (await new FedoraSecurityLab().LoadGalleryItems())[0];

            StringAssert.Contains(item.Name, "Fedora", StringComparison.OrdinalIgnoreCase);
        }

        // ── AthenaOS ─────────────────────────────────────────────────────────────

        [TestMethod]
        public async Task AthenaOS_LoadGalleryItems_ReturnsTwoVariants()
        {
            var result = await new AthenaOS().LoadGalleryItems();

            Assert.AreEqual(2, result.Count,
                "Athena should return an Arch and a Nix entry.");
        }

        [TestMethod]
        public async Task AthenaOS_LoadGalleryItems_BothVariantsSharePinnedIso()
        {
            // Same live ISO boots Dory; the variant is chosen in the installer.
            var items = await new AthenaOS().LoadGalleryItems();

            var uris = items.Select(i => i.DiskUri).Distinct().ToList();
            Assert.AreEqual(1, uris.Count, "Both entries must share one DiskUri");
            StringAssert.Contains(uris[0], "v26.03", StringComparison.Ordinal);
            Assert.IsTrue(uris[0].EndsWith(".iso", StringComparison.OrdinalIgnoreCase),
                $"Expected .iso URI, got: {uris[0]}");

            var checksums = items.Select(i => i.ChecksumUri).Distinct().ToList();
            Assert.AreEqual(1, checksums.Count,
                "Both entries must pin the same checksum file");
            StringAssert.Contains(checksums[0], ".sha256", StringComparison.Ordinal);
        }

        [TestMethod]
        public async Task AthenaOS_LoadGalleryItems_VariantsHaveDistinctNamesAndDistroHints()
        {
            var result = await new AthenaOS().LoadGalleryItems();

            CollectionAssert.AllItemsAreUnique(result.Select(i => i.Name).ToList(),
                "Name is part of the dedupe key; the two variants must differ");

            var archItem = result.Single(i => i.Name == "Athena OS (Arch)");
            var nixItem = result.Single(i => i.Name == "Athena OS (Nix)");

            Assert.AreEqual(LinuxDistro.Arch, archItem.LinuxDistro);
            Assert.AreEqual(LinuxDistro.NixOs, nixItem.LinuxDistro);
        }

        [TestMethod]
        public async Task AthenaOS_LoadGalleryItems_ItemsHaveRequiredFields()
        {
            foreach (var item in await new AthenaOS().LoadGalleryItems())
            {
                Assert.IsFalse(string.IsNullOrEmpty(item.Name));
                Assert.IsFalse(string.IsNullOrEmpty(item.Description));
                Assert.AreEqual("Athena OS Project", item.Publisher, $"Publisher wrong on '{item.Name}'");
                Assert.AreEqual("Security", item.Category, $"Category wrong on '{item.Name}'");
                Assert.AreEqual("HvSocket", item.EnhancedSessionTransportType);
                Assert.AreEqual("false", item.SecureBoot);
                Assert.AreEqual("26.03", item.Version);
                Assert.IsFalse(item.IsRecommended, $"Athena items must not be recommended: '{item.Name}'");
                // The Arch Athena installs Lamco via the fork's pacman
                // package; the Nix Athena has no installable backend.
                Assert.AreEqual(item.LinuxDistro == LinuxDistro.Arch, item.SupportsLamco(),
                    $"Lamco capability mismatch on '{item.Name}' (distro {item.LinuxDistro})");
                Assert.IsFalse(string.IsNullOrEmpty(item.SymbolUri),
                    $"SymbolUri (embedded athena-logo.svg) missing on '{item.Name}'");
                Assert.IsFalse(string.IsNullOrEmpty(item.ThumbnailUri),
                    $"ThumbnailUri (embedded athena-logo.svg) missing on '{item.Name}'");
            }
        }
    }
}
