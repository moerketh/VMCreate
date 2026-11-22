using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using VMCreate;
using VMCreate.Gallery;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Tests for <see cref="RdpDesktopCompatibility"/>, the RDP-backend /
    /// desktop rules the GUI, the CLI and the deployment orchestrator share.
    /// </summary>
    [TestClass]
    public sealed class RdpDesktopCompatibilityTests
    {
        private static readonly IDesktopEnvironmentCatalog[] Catalogs = { new KaliDesktopCatalog() };
        private static GalleryItem Kali() => new() { Name = "Kali", LinuxDistro = LinuxDistro.Kali };

        private static string? Check(GalleryItem item, RdpBackend backend, string? desktopId)
            => RdpDesktopCompatibility.Validate(item, new VmCustomizations { RdpBackend = backend, DesktopEnvironmentId = desktopId }, Catalogs);

        [TestMethod]
        public void Xrdp_OnlyOnX11Desktops()
        {
            Assert.IsNull(Check(Kali(), RdpBackend.Xrdp, null), "stock Xfce is X11");
            Assert.IsNotNull(Check(Kali(), RdpBackend.Xrdp, "kde"));
            Assert.IsNotNull(Check(Kali(), RdpBackend.Xrdp, "gnome"));
        }

        [TestMethod]
        public void Lamco_OnlyOnLamcoCapableDesktops()
        {
            Assert.IsNotNull(Check(Kali(), RdpBackend.Lamco, null), "stock Xfce is X11-only");
            Assert.IsNull(Check(Kali(), RdpBackend.Lamco, "gnome"));
        }

        [TestMethod]
        public void AutoAndNone_AlwaysValid()
        {
            foreach (var desktop in new[] { null, "xfce", "kde", "gnome" })
            {
                Assert.IsNull(Check(Kali(), RdpBackend.Auto, desktop), desktop);
                Assert.IsNull(Check(Kali(), RdpBackend.None, desktop), desktop);
            }
        }

        [TestMethod]
        public void Wayrdp_OnOmarchy_KaliParrotKde_AndKaliGnome()
        {
            var omarchy = new GalleryItem { Name = "Omarchy", LinuxDistro = LinuxDistro.Arch, Tags = new List<string> { "omarchy" } };
            Assert.IsNull(Check(omarchy, RdpBackend.Wayrdp, null));

            // Kali: KDE or GNOME — not the stock Xfce (also the effective
            // desktop when none is chosen).
            Assert.IsNull(Check(Kali(), RdpBackend.Wayrdp, "kde"));
            Assert.IsNull(Check(Kali(), RdpBackend.Wayrdp, "gnome"));
            Assert.IsNotNull(Check(Kali(), RdpBackend.Wayrdp, null));
            Assert.IsNotNull(Check(Kali(), RdpBackend.Wayrdp, "xfce"));

            // Parrot has no catalog: its stock desktop is KDE Plasma.
            var parrot = new GalleryItem { Name = "Parrot", LinuxDistro = LinuxDistro.Parrot };
            Assert.IsNull(Check(parrot, RdpBackend.Wayrdp, null));

            // Not validated elsewhere yet.
            var debian = new GalleryItem { Name = "Debian", LinuxDistro = LinuxDistro.Debian };
            Assert.IsNotNull(Check(debian, RdpBackend.Wayrdp, null));
        }

        [TestMethod]
        public void NoCatalog_XrdpKeepsForceX11Fallback()
        {
            var parrot = new GalleryItem { Name = "Parrot", LinuxDistro = LinuxDistro.Parrot };
            Assert.IsNull(Check(parrot, RdpBackend.Xrdp, null));
        }
    }
}
