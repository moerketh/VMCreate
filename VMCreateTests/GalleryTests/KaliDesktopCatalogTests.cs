using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using VMCreate.Gallery;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Contract tests for <see cref="KaliDesktopCatalog"/> — the first
    /// implementation of <see cref="IDesktopEnvironmentCatalog"/>. The
    /// catalog is offline data that drives the desktop card, the CLI
    /// --desktop flag and DesktopSwitchStep, so its shape (ids, defaults,
    /// Lamco support, RDP coupling) must not drift silently.
    /// </summary>
    [TestClass]
    public sealed class KaliDesktopCatalogTests
    {
        private readonly KaliDesktopCatalog _catalog = new();

        private static GalleryItem Item(LinuxDistro distro) => new() { LinuxDistro = distro };

        [TestMethod]
        public void IsVisibleFor_OnlyForKali()
        {
            Assert.IsTrue(_catalog.IsVisibleFor(Item(LinuxDistro.Kali)));
            Assert.IsFalse(_catalog.IsVisibleFor(Item(LinuxDistro.Ubuntu)));
            Assert.IsFalse(_catalog.IsVisibleFor(Item(LinuxDistro.Debian)));
            Assert.IsFalse(_catalog.IsVisibleFor(null));
        }

        [TestMethod]
        public void Catalog_UsesAptAndShipsThreeOptions()
        {
            Assert.AreEqual(DesktopPackageManager.Apt, _catalog.PackageManager);

            var ids = _catalog.Options.Select(o => o.Id).ToList();
            CollectionAssert.AreEquivalent(new[] { "xfce", "kde", "gnome" }, ids);
        }

        [TestMethod]
        public void Xfce_IsTheShippedDefault_AndForcesX11()
        {
            Assert.IsNotNull(_catalog.Default);
            Assert.AreEqual("xfce", _catalog.Default!.Id);

            var xfce = _catalog.Options.Single(o => o.Id == "xfce");
            Assert.AreEqual(DesktopDisplayServer.X11Only, xfce.DisplayServer,
                "Xfce on Kali is X11-only — its existence is why lamco is not offered for it");
            Assert.IsFalse(xfce.LamcoSupported);
            Assert.IsTrue(xfce.IsDefault, "Xfce is what the gallery image ships with");
            Assert.AreEqual(0, xfce.EstimatedDownloadMb, "the stock desktop is already installed — no download");
        }

        [TestMethod]
        public void WaylandDesktops_SupportLamco_AndPurgeXfce()
        {
            foreach (var id in new[] { "kde", "gnome" })
            {
                var option = _catalog.Options.Single(o => o.Id == id);
                Assert.AreEqual(DesktopDisplayServer.WaylandDefault, option.DisplayServer, id);
                Assert.IsTrue(option.LamcoSupported, $"{id} must support lamco");
                CollectionAssert.Contains(option.Packages.ToList(), $"kali-desktop-{id}", id);
                CollectionAssert.Contains(option.PackagesToRemove.ToList(), "kali-desktop-xfce",
                    $"{id} replaces the shipped Xfce desktop");
                Assert.IsFalse(string.IsNullOrEmpty(option.SessionStarter), $"{id} needs a session starter to pin");
                Assert.IsFalse(string.IsNullOrEmpty(option.DisplayManager), $"{id} needs a display manager to pin");
                Assert.IsTrue(option.EstimatedDownloadMb > 0, $"{id} should carry a download estimate");
            }
        }

        [TestMethod]
        public void OptionIds_AreStableContract()
        {
            // These ids appear in saved customizations, CLI invocations and
            // documentation — renaming one silently breaks both.
            foreach (var option in _catalog.Options)
            {
                Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(option.Id, "[^a-z0-9]"),
                    $"id '{option.Id}' must stay lowercase alphanumeric");
            }
        }

        [TestMethod]
        public void ForItem_ReturnsCatalogForKaliOnly()
        {
            IEnumerable<IDesktopEnvironmentCatalog> catalogs = new[] { _catalog };

            Assert.IsNotNull(catalogs.ForItem(Item(LinuxDistro.Kali)));
            Assert.IsNull(catalogs.ForItem(Item(LinuxDistro.Ubuntu)));
            Assert.IsNull(catalogs.ForItem(null));
        }

        [TestMethod]
        public void Resolve_NullOrDefaultYieldsDefault_UnknownYieldsNull()
        {
            Assert.AreEqual("xfce", _catalog.Resolve(null)!.Id);
            Assert.AreEqual("xfce", _catalog.Resolve("")!.Id);
            Assert.AreEqual("xfce", _catalog.Resolve("xfce")!.Id);
            Assert.AreEqual("kde", _catalog.Resolve("kde")!.Id);
            Assert.AreEqual("gnome", _catalog.Resolve("GNOME")!.Id, "resolution is case-insensitive");

            var unknown = _catalog.Resolve("enlightenment");
            Assert.IsNull(unknown, "unknown ids must resolve to null so callers fail loudly");
        }

        [TestMethod]
        public void Resolve_EmptyCatalog_ReturnsNull()
        {
            IDesktopEnvironmentCatalog? nullCatalog = null;

            Assert.IsNull(nullCatalog!.Resolve("kde"));
        }
    }
}
