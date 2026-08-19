using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMCreate;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Tests for <see cref="LinuxDistro"/> gating: the <see cref="LinuxDistroExtensions.SupportsLamco"/>
    /// extension methods and the <see cref="GalleryItem.LinuxDistro"/> hint used for pre-deployment UI gating.
    /// </summary>
    [TestClass]
    public sealed class LinuxDistroTests
    {
        [TestMethod]
        public void SupportsLamco_TrueForPoCDistros()
        {
            Assert.IsTrue(LinuxDistro.Ubuntu.SupportsLamco());
            Assert.IsTrue(LinuxDistro.Fedora.SupportsLamco());
            Assert.IsTrue(LinuxDistro.Debian.SupportsLamco());
            Assert.IsTrue(LinuxDistro.OpenSuse.SupportsLamco());
            Assert.IsTrue(LinuxDistro.Parrot.SupportsLamco());
        }

        [TestMethod]
        public void SupportsLamco_FalseForUnknown()
        {
            Assert.IsFalse(LinuxDistro.Unknown.SupportsLamco());
        }

        [TestMethod]
        public void GalleryItem_SupportsLamco_TrueWhenDistroSupported()
        {
            var item = new GalleryItem { LinuxDistro = LinuxDistro.Ubuntu };
            Assert.IsTrue(item.SupportsLamco());
        }

        [TestMethod]
        public void GalleryItem_SupportsLamco_FalseWhenDistroUnsupported()
        {
            var item = new GalleryItem { LinuxDistro = LinuxDistro.Unknown };
            Assert.IsFalse(item.SupportsLamco());
        }

        [TestMethod]
        public void GalleryItem_SupportsLamco_FalseWhenItemNull()
        {
            GalleryItem item = null;
            Assert.IsFalse(item.SupportsLamco());
        }

        [TestMethod]
        public void GalleryItem_LinuxDistro_DefaultsToUnknown()
        {
            var item = new GalleryItem();
            Assert.AreEqual(LinuxDistro.Unknown, item.LinuxDistro);
        }
    }
}