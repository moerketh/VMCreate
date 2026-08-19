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
        Parrot
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
        /// distribution. PoC gating: Ubuntu, Fedora, Debian, openSUSE Tumbleweed,
        /// and Parrot Security OS. Expand after validation.
        /// </summary>
        public static bool SupportsLamco(this LinuxDistro distro) =>
            distro is LinuxDistro.Ubuntu
                or LinuxDistro.Fedora
                or LinuxDistro.Debian
                or LinuxDistro.OpenSuse
                or LinuxDistro.Parrot;

        /// <summary>
        /// Convenience overload: returns true when <see cref="GalleryItem.LinuxDistro"/>
        /// indicates a Lamco-supported distribution.
        /// </summary>
        public static bool SupportsLamco(this GalleryItem item) =>
            item?.LinuxDistro.SupportsLamco() == true;
    }
}