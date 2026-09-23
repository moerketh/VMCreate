using VMCreate;

namespace VMCreate.Tests.GalleryTests
{
    [TestClass]
    public sealed class GalleryItemPropertyTests
    {
        // ── IsNativeHyperV auto-detection ────────────────────────────────

        [TestMethod]
        public void IsNativeHyperV_DotHyperVDotInUri_ReturnsTrue()
        {
            var item = new GalleryItem { DiskUri = "https://example.com/WinDev.HyperV.vhdx.zip" };
            Assert.IsTrue(item.IsNativeHyperV);
        }

        [TestMethod]
        public void IsNativeHyperV_MicrosoftDownloadZip_ReturnsTrue()
        {
            var item = new GalleryItem { DiskUri = "https://download.microsoft.com/download/abc/WinDev.vhdx.zip" };
            Assert.IsTrue(item.IsNativeHyperV);
        }

        [TestMethod]
        public void IsNativeHyperV_CanonicalHyperVUri_ReturnsTrue()
        {
            var item = new GalleryItem
            {
                DiskUri = "https://partner-images.canonical.com/hyper-v/desktop/noble/release/current/ubuntu-noble-hyperv-amd64-ubuntu-desktop-hyperv.vhdx.zip"
            };
            Assert.IsTrue(item.IsNativeHyperV);
        }

        [TestMethod]
        public void IsNativeHyperV_CanonicalJammyUri_ReturnsTrue()
        {
            var item = new GalleryItem
            {
                DiskUri = "https://partner-images.canonical.com/hyper-v/desktop/jammy/release/current/ubuntu-jammy-hyperv-amd64-ubuntu-desktop-hyperv.vhdx.zip"
            };
            Assert.IsTrue(item.IsNativeHyperV);
        }

        [TestMethod]
        public void IsNativeHyperV_RegularLinuxVmdk_ReturnsFalse()
        {
            var item = new GalleryItem { DiskUri = "https://example.com/kali-linux-2024.vmdk" };
            Assert.IsFalse(item.IsNativeHyperV);
        }

        [TestMethod]
        public void IsNativeHyperV_CanonicalNonHyperVPath_ReturnsFalse()
        {
            var item = new GalleryItem { DiskUri = "https://partner-images.canonical.com/cloud/noble/current/ubuntu-noble-server.img" };
            Assert.IsFalse(item.IsNativeHyperV);
        }

        [TestMethod]
        public void IsNativeHyperV_ExplicitTrue_OverridesUri()
        {
            var item = new GalleryItem { DiskUri = "https://example.com/something.vmdk", IsNativeHyperV = true };
            Assert.IsTrue(item.IsNativeHyperV);
        }

        [TestMethod]
        public void IsNativeHyperV_NullUri_ReturnsFalse()
        {
            // DiskUri is required by contract; null is only reachable via deserialization gaps.
            // The defensive getter must still return false rather than throw.
            var item = new GalleryItem { DiskUri = null! };
            Assert.IsFalse(item.IsNativeHyperV);
        }

        [TestMethod]
        public void IsNativeHyperV_EmptyUri_ReturnsFalse()
        {
            var item = new GalleryItem { DiskUri = "" };
            Assert.IsFalse(item.IsNativeHyperV);
        }

        // ── HasUnattendedInstallPath ─────────────────────────────────────

        [TestMethod]
        public void HasUnattendedInstallPath_DefaultsToFalse()
        {
            // Loaders opt in explicitly; an unset flag must keep ISO items
            // behind the GUI's "Show ISO installers" toggle.
            Assert.IsFalse(new GalleryItem().HasUnattendedInstallPath);
        }

        [TestMethod]
        public void HasUnattendedInstallPath_SurvivesCacheRoundTrip()
        {
            // The gallery cache serializes GalleryItem verbatim; the flag must
            // round-trip or cached unattended ISOs (Omarchy) would vanish from
            // the default view for the cache TTL after the first refresh.
            var serializer = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
            var items = new List<GalleryItem>
            {
                new() { Name = "Omarchy 4.0.4", DiskUri = "https://iso.omarchy.org/omarchy-4.0.4.iso", HasUnattendedInstallPath = true },
                new() { Name = "Athena OS (Arch)", DiskUri = "https://example.com/athena.iso" },
            };

            string json = System.Text.Json.JsonSerializer.Serialize(items, serializer);
            var roundTripped = System.Text.Json.JsonSerializer.Deserialize<List<GalleryItem>>(json, serializer);

            Assert.IsNotNull(roundTripped);
            Assert.IsTrue(roundTripped[0].HasUnattendedInstallPath);
            Assert.IsFalse(roundTripped[1].HasUnattendedInstallPath);
        }
    }
}
