using System.Collections.Generic;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Desktop environments available on Kali Linux gallery images,
    /// sourced from Kali's documented desktop metapackages
    /// (<c>kali-desktop-{xfce,kde,gnome,...}</c>, see
    /// https://www.kali.org/docs/general-use/metapackages/). Kali's Hyper-V
    /// images ship with Xfce; the other desktops install post-boot via
    /// <c>DesktopSwitchStep</c> with <c>desktop_switch_apt.sh</c>, which
    /// re-verifies metapackage existence against the live package index —
    /// the authoritative source is the distro itself, not this catalog.
    /// </summary>
    public sealed class KaliDesktopCatalog : IDesktopEnvironmentCatalog
    {
        // Switching away from the stock Xfce purges it for real: the meta plus
        // the Xfce core by name. Purging only kali-desktop-xfce (the retired
        // kali_kde_switch.sh behavior) left xfce4 installed, because
        // desktop-base's "... | xfce4" dependency keeps it out of autoremove;
        // xrdp's startwm then still found startxfce4. Once these are gone the
        // rest of Xfce (panel plugins, terminal, libs) autoremoves. lightdm is
        // Xfce's display manager; KDE and GNOME bring sddm/gdm3, and the
        // switch script hands the display manager over before purging.
        private static readonly IReadOnlyList<string> XfceRemoval = new List<string>
        {
            "kali-desktop-xfce", "xfce4", "xfce4-session", "xfwm4", "xfdesktop4",
            "xfce4-panel", "thunar", "lightdm"
        };

        private static readonly DesktopEnvironmentOption Xfce = new()
        {
            Id = "xfce",
            DisplayName = "Xfce",
            Description = "Lightweight stock Kali desktop. X11 only — no Wayland session, so RDP always resolves to xorgxrdp.",
            DisplayServer = DesktopDisplayServer.X11Only,
            LamcoSupported = false,
            IsDefault = true,
            Packages = new List<string> { "kali-desktop-xfce" },
            PackagesToRemove = new List<string>(),
            SessionStarter = "startxfce4",
            DisplayManager = "lightdm",
            EstimatedDownloadMb = 0
        };

        private static readonly DesktopEnvironmentOption Kde = new()
        {
            Id = "kde",
            DisplayName = "KDE Plasma",
            Description = "Full-featured modern desktop, Wayland by default since Plasma 6. Served by wayrdp (Plasma 6.3+); Lamco RDP Server also works.",
            DisplayServer = DesktopDisplayServer.WaylandDefault,
            LamcoSupported = true,
            WayrdpSupported = true,
            IsDefault = false,
            Packages = new List<string> { "kali-desktop-kde" },
            PackagesToRemove = XfceRemoval,
            SessionStarter = "startplasma-wayland",
            DisplayManager = "sddm",
            EstimatedDownloadMb = 1400
        };

        private static readonly DesktopEnvironmentOption Gnome = new()
        {
            Id = "gnome",
            DisplayName = "GNOME",
            Description = "Clean modern desktop, Wayland by default since GNOME 40. Lamco RDP Server works with GNOME 45+.",
            DisplayServer = DesktopDisplayServer.WaylandDefault,
            LamcoSupported = true,
            IsDefault = false,
            Packages = new List<string> { "kali-desktop-gnome" },
            PackagesToRemove = XfceRemoval,
            SessionStarter = "gnome-session",
            DisplayManager = "gdm3",
            EstimatedDownloadMb = 1100
        };

        private static readonly List<DesktopEnvironmentOption> All = new() { Xfce, Kde, Gnome };

        /// <summary>Applies to every Kali gallery item — desktop switching is image-wide, not flavor-specific.</summary>
        public bool IsVisibleFor(GalleryItem? item)
            => item?.LinuxDistro == LinuxDistro.Kali;

        public DesktopEnvironmentOption? Default => Xfce;

        public IReadOnlyList<DesktopEnvironmentOption> Options => All;

        public DesktopPackageManager PackageManager => DesktopPackageManager.Apt;
    }
}
