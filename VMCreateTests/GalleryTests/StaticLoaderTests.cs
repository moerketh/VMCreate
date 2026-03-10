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
            var result = await new PwnCloudOS().LoadGalleryItems();

            Assert.AreEqual(1, result.Count);
        }

        [TestMethod]
        public async Task PwnCloudOS_LoadGalleryItems_ItemHasNonEmptyNameAndUri()
        {
            var item = (await new PwnCloudOS().LoadGalleryItems())[0];

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
    }
}
