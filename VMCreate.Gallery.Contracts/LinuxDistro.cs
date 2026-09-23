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
        /// pacman-family distributions (Arch, Athena Arch, Manjaro and other
        /// ID_LIKE=arch derivatives). Not Lamco-capable yet: the fork pipeline
        /// ships no pacman package, so Auto resolves these guests to xrdp.
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
        /// distribution. The install path is a pinned amd64 Debian deb; the
        /// fork release pipeline does not build rpm/flatpak assets for this
        /// lineage. Kali is Debian-family (ID_LIKE=debian, dpkg/apt) and the
        /// pinned deb path works there, including the Kali-KDE image whose
        /// KDE/Plasma 6 desktop defaults to Wayland — the strongest Auto→Lamco
        /// candidate. Fedora/openSUSE regain Lamco support when the fork
        /// pipeline ships rpms for them; Arch likewise once the fork
        /// pipeline ships a pacman package (Athena Arch included).
        /// </summary>
        public static bool SupportsLamco(this LinuxDistro distro) =>
            distro is LinuxDistro.Ubuntu
                or LinuxDistro.Debian
                or LinuxDistro.Parrot
                or LinuxDistro.Kali;

        /// <summary>
        /// Convenience overload: returns true when <see cref="GalleryItem.LinuxDistro"/>
        /// indicates a Lamco-supported distribution. Null-safe: a null item
        /// (or one without a distro hint) is not Lamco-capable.
        /// </summary>
        public static bool SupportsLamco(this GalleryItem? item) =>
            item?.LinuxDistro.SupportsLamco() == true;
    }
}
