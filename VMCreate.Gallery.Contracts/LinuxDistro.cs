using System;

namespace VMCreate
{
    /// <summary>
    /// Identifies the Linux distribution family of a <see cref="GalleryItem"/>, used
    /// to gate distribution-specific customization steps (e.g. Lamco RDP Server is
    /// only supported on a subset of recent distributions). Populated by gallery
    /// loaders as metadata so the pre-deployment UI can show/hide options without a
    /// live SSH shell. <see cref="DistroDetector"/> re-verifies at execution time.
    /// </summary>
    public enum LinuxDistro
    {
        /// <summary>Unknown / not a Linux gallery item, or distro not yet classified.</summary>
        Unknown,

        Ubuntu,
        Debian,
        Fedora,
        OpenSuse,
        Parrot,
        Kali,

        /// <summary>
        /// RHEL-compatible family (Rocky, AlmaLinux, and other ID_LIKE=rhel
        /// derivatives). rpm/dnf-based; the Elastic SIEM appliance (Rocky 10
        /// GenericCloud) lives here.
        /// </summary>
        Rocky,

        /// <summary>
        /// pacman-family distributions (Arch, Athena Arch, Manjaro and other
        /// ID_LIKE=arch derivatives). Lamco-capable via the fork pipeline's
        /// amd64 Arch pacman package (lamco-rdp-server-1.4.5-hyperv6-1),
        /// which Omarchy (Arch + Hyprland) and Athena Arch install through
        /// pacman -U.
        /// </summary>
        Arch,

        /// <summary>
        /// Declarative Nix-family distributions (NixOS, Athena Nix
        /// &mdash; ID=athenaos). Post-boot service installs via a package
        /// manager are structurally impossible there; Nix automation would
        /// have to flow through the installer's config instead.
        /// </summary>
        NixOs
    }

    /// <summary>
    /// Extension methods for <see cref="LinuxDistro"/> and <see cref="GalleryItem"/>
    /// distro gating. Kept in the contracts assembly so both gallery loaders and
    /// customization steps can use it without a circular reference.
    /// </summary>
    public static class LinuxDistroExtensions
    {
        /// <summary>
        /// Returns true if the Lamco RDP Server backend is supported on this
        /// distribution. The install path is a pinned amd64 Debian deb on the
        /// Debian lineage and a pinned amd64 Arch pacman package on the pacman
        /// lineage (Omarchy, Athena Arch); the fork release pipeline does not
        /// build rpm/flatpak assets for this lineage. Kali is Debian-family
        /// (ID_LIKE=debian, dpkg/apt) and the pinned deb path works there,
        /// including the Kali-KDE image whose KDE/Plasma 6 desktop defaults
        /// to Wayland — the strongest Auto→Lamco candidate. Fedora/openSUSE
        /// regain Lamco support when the fork pipeline ships rpms for them.
        /// </summary>
        public static bool SupportsLamco(this LinuxDistro distro) =>
            distro is LinuxDistro.Ubuntu
                or LinuxDistro.Debian
                or LinuxDistro.Parrot
                or LinuxDistro.Kali
                or LinuxDistro.Arch;

        /// <summary>
        /// Convenience overload: returns true when <see cref="GalleryItem.LinuxDistro"/>
        /// indicates a Lamco-supported distribution. Null-safe: a null item
        /// (or one without a distro hint) is not Lamco-capable.
        /// </summary>
        public static bool SupportsLamco(this GalleryItem? item) =>
            item?.LinuxDistro.SupportsLamco() == true;

        /// <summary>
        /// Returns true if the wayrdp backend is targetable on this
        /// distribution. wayrdp is the project's own MIT Wayland-native RDP
        /// server on FreeRDP 3; its packaging line follows the same amd64
        /// Debian-deb lineage as the Lamco fork, so the supported set is
        /// identical for now. (During Phase 0 the install step fails loudly
        /// regardless of distro — see <c>install_wayrdp.sh</c>.)
        /// </summary>
        public static bool SupportsWayrdp(this LinuxDistro distro) =>
            distro.SupportsLamco();

        /// <summary>
        /// Convenience overload: returns true when <see cref="GalleryItem.LinuxDistro"/>
        /// indicates a wayrdp-targetable distribution. Null-safe.
        /// </summary>
        public static bool SupportsWayrdp(this GalleryItem? item) =>
            item?.LinuxDistro.SupportsWayrdp() == true;
    }
}
