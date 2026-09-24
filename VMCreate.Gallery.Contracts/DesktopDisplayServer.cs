namespace VMCreate
{
    /// <summary>
    /// The display-server reality of a desktop environment — the coupling
    /// point between the desktop choice and the RDP backend. Lamco
    /// (<see cref="RdpBackend.Lamco"/>) is Wayland-native and needs a graphical
    /// Wayland session; xrdp disables Wayland and forces X11. An X11-only
    /// desktop (e.g. Xfce) therefore forces the RDP solution to xorgxrdp.
    /// </summary>
    public enum DesktopDisplayServer
    {
        /// <summary>
        /// A fresh graphical login lands on Wayland by default (GNOME 45+,
        /// KDE Plasma 6). Lamco-capable when the distro also supports it.
        /// </summary>
        WaylandDefault,

        /// <summary>
        /// No Wayland session exists for this desktop (Xfce, MATE, LXQt,
        /// LXDE, Budgie, Cinnamon default) — X11 always. The Lamco RDP
        /// backend cannot serve these desktops.
        /// </summary>
        X11Only,

        /// <summary>
        /// A Wayland session exists but is experimental/preview-tier
        /// (e.g. Cinnamon with experimental Wayland enabled) — not
        /// something a default deployment should rely on.
        /// </summary>
        WaylandExperimental
    }

    /// <summary>
    /// The native package manager the desktop switch must drive in the
    /// guest. Determines which family script runs
    /// (e.g. <c>desktop_switch_apt.sh</c>) and how metapackage existence
    /// is probed at deployment time.
    /// </summary>
    public enum DesktopPackageManager
    {
        /// <summary>Debian family: Kali, Ubuntu, Debian, Mint (apt/dpkg).</summary>
        Apt,

        /// <summary>Fedora family: Fedora, Rocky (dnf groups).</summary>
        Dnf,

        /// <summary>openSUSE (zypper patterns).</summary>
        Zypper,

        /// <summary>Arch (pacman groups).</summary>
        Pacman
    }
}
