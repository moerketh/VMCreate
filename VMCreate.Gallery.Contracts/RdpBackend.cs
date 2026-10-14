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
        /// wayrdp — the project's own MIT-licensed Wayland-native RDP server
        /// built on FreeRDP 3 (<c>libfreerdp-server</c>), intended to replace
        /// the Lamco fork. Captures via compositor-specific backends, selected
        /// at runtime via <c>WAYRDP_BACKEND</c> (wlroots: a headless output at
        /// the exact client size captured via ext-image-copy-capture — the
        /// path that lands Hyprland parity; kde: KWin virtual outputs +
        /// PipeWire via the shared <c>hyperv-rdp-extras</c> crate; synthetic
        /// for headless smoke tests).
        /// <para>
        /// The package is fetched from the wayrdp repo's public releases: the
        /// install script curls the pinned release asset
        /// (<c>releases/latest/download/wayrdp-&lt;ver&gt;-linux-x86_64.tar.gz</c>)
        /// and verifies its sha256 against the digest pinned in the script
        /// before extracting and running the bundle's install.sh (prefix
        /// /usr/local) — the same pinned-URL-and-digest contract as the Lamco
        /// fork's curl flow. A download or digest mismatch fails the
        /// deployment loudly (no fallback paths — a silently absent or
        /// re-pointed RDP server is the worse failure). The script prints a
        /// machine-readable <c>WAYRDP_RESULT=ok|degraded</c> terminal line;
        /// install-time readiness is the systemd user unit being active,
        /// with a session-absent state reported as DEGRADED rather than a
        /// hard error (mirrors the Lamco gate).
        /// </para>
        /// Requires a graphical Wayland session to share, so graphical
        /// autologin is enabled. Supported distro families: Debian trixie+
        /// (Ubuntu, Debian, Parrot, Kali — via libfreerdp3 runtime libs) and
        /// Arch-family (incl. Omarchy — via the <c>freerdp</c> package); the
        /// wlroots backend additionally requires <c>hyprctl</c> on the PATH.
        /// </summary>
        Wayrdp,

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