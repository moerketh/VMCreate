using System.Collections.Generic;

namespace VMCreate
{
    /// <summary>
    /// One switchable desktop environment offered by a distribution's
    /// <see cref="IDesktopEnvironmentCatalog"/>. Describes the distro-native
    /// metapackage(s) that install the desktop, the session starter that
    /// should become the default, and the display-server reality that drives
    /// RDP-backend coupling in the UI.
    /// </summary>
    public sealed class DesktopEnvironmentOption
    {
        /// <summary>
        /// Stable machine id ("kde", "gnome", "xfce", …). Stored in
        /// <see cref="VmCustomizations.DesktopEnvironmentId"/> and used as
        /// the CLI <c>--desktop</c> value. Must stay stable once shipped.
        /// </summary>
        public string Id { get; init; } = "";

        /// <summary>Human-readable name shown in the UI ("KDE Plasma").</summary>
        public string DisplayName { get; init; } = "";

        /// <summary>One-line description of the desktop for the UI.</summary>
        public string Description { get; init; } = "";

        /// <summary>
        /// The display server a fresh login on this desktop lands on. Drives
        /// the Lamco/xrdp coupling in the customization UI and the Auto-hint.
        /// </summary>
        public DesktopDisplayServer DisplayServer { get; init; }

        /// <summary>
        /// True when the Lamco RDP Server can serve this desktop — requires
        /// a Wayland session AND a supported portal implementation
        /// (GNOME 45+, KDE Plasma 6.3+).
        /// </summary>
        public bool LamcoSupported { get; init; }

        /// <summary>
        /// True when this is the desktop the gallery image already ships
        /// with. Selecting the default (or nothing at all) performs no
        /// switch; it only pins the RDP-backend coupling hints.
        /// </summary>
        public bool IsDefault { get; init; }

        /// <summary>
        /// Distro-native metapackage names that install the desktop
        /// (e.g. "kali-desktop-kde", "kubuntu-desktop"). Probed in the guest
        /// before installing — a missing metapackage fails the deployment
        /// loudly instead of silently shipping the stock desktop.
        /// </summary>
        public IReadOnlyList<string> Packages { get; init; } = new List<string>();

        /// <summary>
        /// Metapackages removed after the switch (the stock desktop's meta,
        /// e.g. "kali-desktop-xfce"). Empty for the keep-old-desktop policy
        /// (Phase 2 distros); Kali keeps its historical purge behavior.
        /// Removal failures are degraded, not fatal.
        /// </summary>
        public IReadOnlyList<string> PackagesToRemove { get; init; } = new List<string>();

        /// <summary>
        /// Session starter that becomes the default graphical session
        /// ("startplasma-wayland", "gnome-session", "startxfce4", …). May be
        /// null when the display manager alone decides (GDM + GNOME).
        /// </summary>
        public string? SessionStarter { get; init; }

        /// <summary>
        /// Display manager this desktop expects ("sddm", "gdm3", "lightdm").
        /// Installing a desktop pulls its DM as a dependency; the switch
        /// script pins the system default to this value so autologin and
        /// display-server detection see a coherent stack. Null = keep.
        /// </summary>
        public string? DisplayManager { get; init; }

        /// <summary>
        /// Rough download size shown as a hint in the UI (MB). Advisory
        /// only; the guest's package manager decides the real number.
        /// </summary>
        public int EstimatedDownloadMb { get; init; }
    }
}
