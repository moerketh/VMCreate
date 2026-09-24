using System.Collections.Generic;

namespace VMCreate
{
    /// <summary>
    /// The set of desktop environments a distribution's gallery images can be
    /// switched to post-boot. Ships in the same assembly as the distro's
    /// gallery loader (e.g. the Kali catalog in VMCreate.Gallery.Security) and
    /// is discovered via the same assembly scanning as customization steps —
    /// both front ends (GUI and CLI) register it via DI.
    /// <para>
    /// This is the static, offline layer of desktop availability: the
    /// catalog proposes the distro's documented metapackages, and the
    /// in-guest switch script re-verifies against the live package index
    /// at deployment time (the distro disposes). Pre-boot there is no
    /// package manager to ask, and download pages are not a stable source.
    /// </para>
    /// </summary>
    public interface IDesktopEnvironmentCatalog : IStepVisibility
    {
        /// <summary>The desktop the image ships with (never installed — it is already there).</summary>
        DesktopEnvironmentOption? Default { get; }

        /// <summary>All switchable desktops for this distribution, including the default.</summary>
        IReadOnlyList<DesktopEnvironmentOption> Options { get; }

        /// <summary>The package manager the switch script must drive in the guest.</summary>
        DesktopPackageManager PackageManager { get; }
    }

    /// <summary>
    /// Lookup helpers over the discovered <see cref="IDesktopEnvironmentCatalog"/>
    /// implementations — shared by the customization UI, the CLI, and
    /// <c>DesktopSwitchStep</c> so all three resolve desktop ids identically.
    /// </summary>
    public static class DesktopEnvironmentCatalogExtensions
    {
        /// <summary>
        /// Returns the catalog that applies to this gallery item, or null
        /// when the distro ships no catalog (desktop switching not offered —
        /// the Desktop Environment card is hidden and DesktopSwitchStep is
        /// not applicable).
        /// </summary>
        public static IDesktopEnvironmentCatalog? ForItem(
            this IEnumerable<IDesktopEnvironmentCatalog>? catalogs, GalleryItem? item)
        {
            if (item == null || catalogs == null) return null;
            foreach (var catalog in catalogs)
            {
                if (catalog.IsVisibleFor(item))
                    return catalog;
            }
            return null;
        }

        /// <summary>
        /// Resolves a desktop id against a catalog. Null id (keep default)
        /// yields the <see cref="IDesktopEnvironmentCatalog.Default"/> entry
        /// when present; an unknown id yields null — callers fail loudly
        /// ("no such desktop for this image") rather than guessing.
        /// </summary>
        public static DesktopEnvironmentOption? Resolve(
            this IDesktopEnvironmentCatalog catalog, string? desktopEnvironmentId)
        {
            if (catalog == null) return null;
            if (string.IsNullOrWhiteSpace(desktopEnvironmentId))
                return catalog.Default;

            foreach (var option in catalog.Options)
            {
                if (string.Equals(option.Id, desktopEnvironmentId, System.StringComparison.OrdinalIgnoreCase))
                    return option;
            }
            return null;
        }
    }
}
