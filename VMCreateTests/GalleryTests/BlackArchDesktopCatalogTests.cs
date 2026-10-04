using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using VMCreate.Gallery;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Contract tests for <see cref="BlackArchDesktopCatalog"/> — the pacman
    /// implementation of <see cref="IDesktopEnvironmentCatalog"/>. The
    /// BlackArch item is a headless Arch base (tag <c>blackarch</c>), so its
    /// desktop catalog differs from Kali's in two pinned ways: the default is
    /// "no desktop" (the base ships none), and the catalog drives
    /// <c>desktop_switch_pacman.sh</c> rather than the apt script. These are
    /// the offline facts the wizard desktop card, the CLI and
    /// DesktopSwitchStep all resolve from.
    /// </summary>
    [TestClass]
    public sealed class BlackArchDesktopCatalogTests
    {
        private readonly BlackArchDesktopCatalog _catalog = new();

        private static GalleryItem BlackArchItem() => new()
        {
            Name = "BlackArch",
            LinuxDistro = LinuxDistro.Arch,
            Tags = new List<string> { "blackarch" }
        };

        private static GalleryItem PlainArchItem() => new()
        {
            Name = "Arch",
            LinuxDistro = LinuxDistro.Arch
        };

        [TestMethod]
        public void IsVisibleFor_OnlyForBlackArchTaggedItem()
        {
            Assert.IsTrue(_catalog.IsVisibleFor(BlackArchItem()));
            Assert.IsFalse(_catalog.IsVisibleFor(PlainArchItem()),
                "plain Arch carries no blackarch tag — no desktop catalog offered there");
            Assert.IsFalse(_catalog.IsVisibleFor(new GalleryItem { LinuxDistro = LinuxDistro.Kali }));
            Assert.IsFalse(_catalog.IsVisibleFor(null));
        }

        [TestMethod]
        public void Catalog_UsesPacmanAndShipsNoDesktopPlusThreeDesktops()
        {
            Assert.AreEqual(DesktopPackageManager.Pacman, _catalog.PackageManager,
                "the Arch lineage is pacman — the switch must run desktop_switch_pacman.sh");

            var ids = _catalog.Options.Select(o => o.Id).ToList();
            CollectionAssert.AreEquivalent(new[] { "no-desktop", "xfce", "kde", "gnome" }, ids);
        }

        [TestMethod]
        public void NoDesktop_IsTheHeadlessDefault()
        {
            Assert.IsNotNull(_catalog.Default);
            Assert.AreEqual("no-desktop", _catalog.Default!.Id,
                "the BlackArch (Arch) base is headless — the default installs no desktop");

            var noDesktop = _catalog.Options.Single(o => o.Id == "no-desktop");
            Assert.IsTrue(noDesktop.IsDefault);
            Assert.AreEqual(0, noDesktop.Packages.Count,
                "no desktop metapackages are installed for the headless option");
            Assert.IsTrue(string.IsNullOrEmpty(noDesktop.DisplayManager));
            Assert.AreEqual(0, noDesktop.EstimatedDownloadMb);
            Assert.IsFalse(noDesktop.LamcoSupported,
                "headless has no session for a Wayland-native Lamco RDP to capture");
        }

        [TestMethod]
        public void Xfce_ForcesX11_AndNoLamco()
        {
            var xfce = _catalog.Options.Single(o => o.Id == "xfce");
            Assert.AreEqual(DesktopDisplayServer.X11Only, xfce.DisplayServer,
                "Xfce is X11-only — RDP resolves to xorgxrdp, never Lamco");
            Assert.IsFalse(xfce.LamcoSupported,
                "Lamco is Wayland-native and cannot serve an X11 desktop");
            Assert.IsFalse(xfce.IsDefault);
            CollectionAssert.Contains(xfce.Packages.ToList(), "xfce4");
            CollectionAssert.Contains(xfce.Packages.ToList(), "lightdm");
            Assert.AreEqual("startxfce4", xfce.SessionStarter);
            Assert.AreEqual("lightdm", xfce.DisplayManager);
        }

        [TestMethod]
        public void WaylandDesktops_SupportLamco_ViaArchPacmanPackage()
        {
            // The Arch lineage is Lamco-capable (the fork pipeline ships an
            // Arch pacman package installed via pacman -U), so KDE/GNOME —
            // Wayland-default — carry LamcoSupported=true, matching Kali.
            foreach (var id in new[] { "kde", "gnome" })
            {
                var option = _catalog.Options.Single(o => o.Id == id);
                Assert.AreEqual(DesktopDisplayServer.WaylandDefault, option.DisplayServer, id);
                Assert.IsTrue(option.LamcoSupported, $"{id} must support lamco on the Arch pacman lineage");
                Assert.IsFalse(option.IsDefault, $"{id} is not what the headless base ships");
                Assert.IsTrue(option.Packages.Count > 0, $"{id} needs pacman packages/groups to install");
                Assert.IsFalse(string.IsNullOrEmpty(option.DisplayManager), $"{id} needs a display manager to pin");
                Assert.IsTrue(option.EstimatedDownloadMb > 0, $"{id} should carry a download estimate");
            }

            CollectionAssert.Contains(_catalog.Options.Single(o => o.Id == "kde").Packages.ToList(), "plasma");
            CollectionAssert.Contains(_catalog.Options.Single(o => o.Id == "gnome").Packages.ToList(), "gnome");
            Assert.AreEqual("sddm", _catalog.Options.Single(o => o.Id == "kde").DisplayManager);
            Assert.AreEqual("gdm", _catalog.Options.Single(o => o.Id == "gnome").DisplayManager);
        }

        [TestMethod]
        public void OptionIds_AreStableContract()
        {
            foreach (var option in _catalog.Options)
            {
                Assert.IsTrue(
                    System.Text.RegularExpressions.Regex.IsMatch(option.Id, "^[a-z0-9-]+$"),
                    $"id '{option.Id}' must stay lowercase alphanumeric/kebab (stored in saved customizations + CLI)");
            }
        }

        [TestMethod]
        public void ForItem_ReturnsCatalogForBlackArchOnly()
        {
            IEnumerable<IDesktopEnvironmentCatalog> catalogs = new IDesktopEnvironmentCatalog[]
            {
                new KaliDesktopCatalog(),
                _catalog
            };

            Assert.AreSame(_catalog, catalogs.ForItem(BlackArchItem()));
            Assert.IsNull(catalogs.ForItem(PlainArchItem()));
            Assert.IsNull(catalogs.ForItem(null));
        }

        [TestMethod]
        public void Resolve_NullOrDefaultYieldsNoDesktop_UnknownYieldsNull()
        {
            Assert.AreEqual("no-desktop", _catalog.Resolve(null)!.Id);
            Assert.AreEqual("no-desktop", _catalog.Resolve("")!.Id);
            Assert.AreEqual("no-desktop", _catalog.Resolve("no-desktop")!.Id);
            Assert.AreEqual("xfce", _catalog.Resolve("xfce")!.Id);
            Assert.AreEqual("kde", _catalog.Resolve("kde")!.Id);
            Assert.AreEqual("gnome", _catalog.Resolve("GNOME")!.Id, "resolution is case-insensitive");

            Assert.IsNull(_catalog.Resolve("enlightenment"),
                "unknown ids must resolve to null so callers fail loudly");
        }
    }
}
