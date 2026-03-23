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
            var item = new GalleryItem { DiskUri = null };
            Assert.IsFalse(item.IsNativeHyperV);
        }

        [TestMethod]
        public void IsNativeHyperV_EmptyUri_ReturnsFalse()
        {
            var item = new GalleryItem { DiskUri = "" };
            Assert.IsFalse(item.IsNativeHyperV);
        }
    }
}
