using System.Collections.Generic;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Desktop environments available on the BlackArch gallery item — an Arch
    /// Linux base (tag <c>blackarch</c>) that ships <b>headless</b>. Unlike
    /// Kali (whose Hyper-V image ships with Xfce), there is no pre-installed
    /// desktop, so the default is "No desktop" and every graphical option is
    /// installed post-boot by <c>DesktopSwitchStep</c> with
    /// <c>desktop_switch_pacman.sh</c>, which re-verifies package/group
    /// existence against the live pacman databases — the authoritative source
    /// is the distro itself, not this catalog.
    /// <para>
    /// <see cref="DesktopEnvironmentOption.LamcoSupported"/> is
    /// <see langword="true"/> on the Wayland-default options (KDE, GNOME): on
    /// the Arch lineage the Lamco RDP Server installs from the fork pipeline's
    /// pacman package (<c>pacman -U</c>), and these desktops ship a Wayland
    /// session Lamco can capture via Portal + PipeWire. It is
    /// <see langword="false"/> on <c>no-desktop</c> (headless — no graphical
    /// session exists for a Lamco/RDP backend to serve) and on <c>xfce</c>
    /// (X11-only — Lamco is Wayland-native). Those options route to
    /// <c>xrdp</c>/SSH instead.
    /// </para>
    /// </summary>
    public sealed class BlackArchDesktopCatalog : IDesktopEnvironmentCatalog
    {
        // The Arch base is headless: no desktop meta is ever installed, the
        // default stays a text console. DesktopSwitchStep's pacman script
        // no-ops this option (nothing to probe, install, or hand over), so
        // selecting it only pins the RDP/backend coupling hints.
        private static readonly DesktopEnvironmentOption NoDesktop = new()
        {
            Id = "no-desktop",
            DisplayName = "No desktop",
            Description = "Headless — the stock Arch cloud image. No desktop environment is installed; access over SSH only (no RDP backend).",
            DisplayServer = DesktopDisplayServer.X11Only,
            LamcoSupported = false,
            IsDefault = true,
            Packages = new List<string>(),
            PackagesToRemove = new List<string>(),
            SessionStarter = null,
            DisplayManager = null,
            EstimatedDownloadMb = 0
        };

        private static readonly DesktopEnvironmentOption Xfce = new()
        {
            Id = "xfce",
            DisplayName = "Xfce",
            Description = "Lightweight desktop. X11 only — no Wayland session, so RDP always resolves to xorgxrdp.",
            DisplayServer = DesktopDisplayServer.X11Only,
            LamcoSupported = false,
            IsDefault = false,
            Packages = new List<string> { "xfce4", "lightdm", "lightdm-gtk-greeter" },
            PackagesToRemove = new List<string>(),
            SessionStarter = "startxfce4",
            DisplayManager = "lightdm",
            EstimatedDownloadMb = 600
        };

        private static readonly DesktopEnvironmentOption Kde = new()
        {
            Id = "kde",
            DisplayName = "KDE Plasma",
            Description = "Full-featured modern desktop, Wayland by default since Plasma 6. Lamco RDP Server works with Plasma 6.3+.",
            DisplayServer = DesktopDisplayServer.WaylandDefault,
            LamcoSupported = true,
            IsDefault = false,
            Packages = new List<string> { "plasma", "sddm" },
            PackagesToRemove = new List<string>(),
            SessionStarter = "startplasma-wayland",
            DisplayManager = "sddm",
            EstimatedDownloadMb = 1500
        };

        private static readonly DesktopEnvironmentOption Gnome = new()
        {
            Id = "gnome",
            DisplayName = "GNOME",
            Description = "Clean modern desktop, Wayland by default since GNOME 40. Lamco RDP Server works with GNOME 45+.",
            DisplayServer = DesktopDisplayServer.WaylandDefault,
            LamcoSupported = true,
            IsDefault = false,
            Packages = new List<string> { "gnome", "gdm" },
            PackagesToRemove = new List<string>(),
            SessionStarter = "gnome-session",
            DisplayManager = "gdm",
            EstimatedDownloadMb = 1200
        };

        private static readonly List<DesktopEnvironmentOption> All = new() { NoDesktop, Xfce, Kde, Gnome };

        /// <summary>Applies only to the BlackArch gallery item (plain Arch carries no catalog).</summary>
        public bool IsVisibleFor(GalleryItem? item)
            => item.HasTag("blackarch");

        /// <summary>The Arch base is headless, so the default installs no desktop.</summary>
        public DesktopEnvironmentOption? Default => NoDesktop;

        public IReadOnlyList<DesktopEnvironmentOption> Options => All;

        public DesktopPackageManager PackageManager => DesktopPackageManager.Pacman;
    }
}
