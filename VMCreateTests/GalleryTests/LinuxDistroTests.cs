using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
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
        public void SupportsLamco_TrueForDebAndPacmanFamilyDistros()
        {
            // The Lamco install path is a pinned amd64 Debian deb on the
            // Debian lineage and a pinned amd64 pacman package on the Arch
            // lineage (Omarchy Phase D shipped the fork's Arch package); rpm
            // distros are gated off until the fork pipeline ships rpms.
            Assert.IsTrue(LinuxDistro.Ubuntu.SupportsLamco());
            Assert.IsTrue(LinuxDistro.Debian.SupportsLamco());
            Assert.IsTrue(LinuxDistro.Parrot.SupportsLamco());
            Assert.IsTrue(LinuxDistro.Kali.SupportsLamco());
            Assert.IsTrue(LinuxDistro.Arch.SupportsLamco());
        }

        [TestMethod]
        public void SupportsLamco_FalseForRpmDistros()
        {
            Assert.IsFalse(LinuxDistro.Fedora.SupportsLamco());
            Assert.IsFalse(LinuxDistro.OpenSuse.SupportsLamco());
        }

        [TestMethod]
        public void SupportsLamco_FalseForNixDistros()
        {
            // NixOs (incl. Athena Nix / NixOS): declarative distro — no
            // package-manager install path possible. Arch-family distros
            // ARE supported since the fork pipeline ships an amd64 pacman
            // package (lamco-rdp-server-1.4.5-hyperv6-1; Omarchy Phase D).
            Assert.IsFalse(LinuxDistro.NixOs.SupportsLamco());
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
            GalleryItem? item = null;
            Assert.IsFalse(item.SupportsLamco());
        }

        [TestMethod]
        public void GalleryItem_LinuxDistro_DefaultsToUnknown()
        {
            var item = new GalleryItem();
            Assert.AreEqual(LinuxDistro.Unknown, item.LinuxDistro);
        }

        [TestMethod]
        public void SupportsWayrdp_MatchesSupportsLamcoSet()
        {
            // wayrdp's packaging line follows the same amd64 Debian-deb
            // lineage — the supported set is defined as identical.
            foreach (LinuxDistro distro in Enum.GetValues(typeof(LinuxDistro)))
            {
                Assert.AreEqual(distro.SupportsLamco(), distro.SupportsWayrdp(),
                    $"SupportsWayrdp diverged from SupportsLamco for {distro}");
            }
        }

        [TestMethod]
        public void GalleryItem_SupportsWayrdp_NullSafe()
        {
            GalleryItem? item = null;
            Assert.IsFalse(item.SupportsWayrdp());
            item = new GalleryItem { LinuxDistro = LinuxDistro.Ubuntu };
            Assert.IsTrue(item.SupportsWayrdp());
        }
    }
}