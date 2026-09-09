namespace VMCreate
{
    /// <summary>
    /// Selects which RDP server backend is installed and configured on a Linux guest
    /// for Hyper-V Enhanced Session / remote desktop access.
    /// </summary>
    public enum RdpBackend
    {
        /// <summary>
        /// No RDP server is installed. The guest is accessed only via the Hyper-V
        /// console or SSH. Default for users who explicitly disable remote desktop.
        /// </summary>
        None,

        /// <summary>
        /// The classic xrdp server, installed via the external cloning ISO
        /// (<c>install_xrdp.sh</c> in the hyperv-convert-iso repo) and fixed up
        /// post-boot. Disables Wayland and forces X11 because <c>hyperv_drm</c>
        /// has incomplete atomic modesetting support. Default for maximum
        /// compatibility across Linux distributions.
        /// </summary>
        Xrdp,

        /// <summary>
        /// Lamco RDP Server — a Wayland-native RDP server built on IronRDP with
        /// XDG Desktop Portal + PipeWire screen capture. Keeps Wayland enabled
        /// (no X11 fallback) and installs post-boot over SSH from a pinned fork
        /// release deb (sha256-verified; no fallback paths — a missing asset
        /// fails the deployment loudly). Requires a graphical Wayland session
        /// to share, so graphical autologin is enabled; the one-time Portal
        /// consent grant is automated via a systemd oneshot unit.
        /// Debian-family distros only (Ubuntu, Debian, Parrot) — the fork
        /// pipeline ships amd64 debs. Supported on recent GNOME 45+ and
        /// KDE Plasma 6.3+ desktops.
        /// </summary>
        Lamco
    }
}