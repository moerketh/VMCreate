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
        /// Debian-family distros only (Ubuntu, Debian, Parrot, Kali) — the fork
        /// pipeline ships amd64 debs. Supported on recent GNOME 45+ and
        /// KDE Plasma 6.3+ desktops.
        /// </summary>
        Lamco,

        /// <summary>
        /// Auto-select: the backend is resolved at deployment time instead of
        /// chosen statically. A detection step runs post-boot over SSH, inspects
        /// the guest's actual display-server state (Wayland session present,
        /// session-manager alternatives) and distro, and rewrites this value to
        /// either <see cref="Lamco"/> (Wayland-default desktop on a
        /// Debian-family distro) or <see cref="Xrdp"/> (everything else).
        /// <para>
        /// While in this state the deployment pipeline behaves conservatively:
        /// pre-boot customization is skipped (no <c>VMCREATE_XRDP</c> KVP, so
        /// the cloning-ISO chroot never pre-installs xrdp — xrdp and Lamco
        /// conflict on port 3389), and the backend-dependent post-boot steps
        /// re-evaluate their applicability just-in-time as the resolver runs.
        /// When the resolution lands on Xrdp, a post-boot xrdp install step
        /// backfills the install that the skipped chroot would have done.
        /// </para>
        /// <para>
        /// Defaults to Xrdp when detection cannot produce a verdict (SSH
        /// transport failure), preserving the maximum-compatibility behavior.
        /// </para>
        /// </summary>
        Auto
    }
}