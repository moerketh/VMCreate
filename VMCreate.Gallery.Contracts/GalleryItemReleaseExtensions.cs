using System;
using System.Linq;

namespace VMCreate
{
    /// <summary>
    /// Helper methods for working with gallery item release variants.
    /// </summary>
    public static class GalleryItemReleaseExtensions
    {
        /// <summary>
        /// True for cache entries written before this distro collapsed its
        /// stable/weekly gallery twins into a single item with release
        /// variants (currently Kali). Such entries are dropped when loading
        /// a cache — the live source now supplies the multi-release item.
        /// </summary>
        public static bool IsLegacyReleaseCacheEntry(this GalleryItem item) =>
            item.LinuxDistro == LinuxDistro.Kali
            && item.ReleaseVariants is not { Count: > 0 };

        /// <summary>
        /// Returns the release variant with the supplied id (case-insensitive).
        /// A null or empty id yields the default variant; an unknown id yields
        /// null so callers fail loudly listing the offered ids.
        /// </summary>
        public static ReleaseVariant? FindReleaseVariant(this GalleryItem? item, string? id)
        {
            if (item?.ReleaseVariants is not { Count: > 0 } variants)
                return null;

            if (string.IsNullOrEmpty(id))
                return variants.FirstOrDefault(v => v.IsDefault) ?? variants[0];

            return variants.FirstOrDefault(v =>
                string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolves the supplied release id (null/empty = default) to an
        /// effective gallery item whose <c>DiskUri</c>, <c>Version</c>,
        /// <c>LastUpdated</c> and <c>Description</c> describe the chosen
        /// release. Items without variants resolve a null id to themselves;
        /// an explicit id on such an item yields null. A null id always
        /// resolves to the item itself — the published fields stay
        /// authoritative, even for a malformed variant list with no default
        /// marker — and the default variant does too; the returned clone for
        /// a non-default variant carries no variant list — it is a concrete
        /// downloadable image, not a picker.
        /// </summary>
        public static GalleryItem? ResolveRelease(this GalleryItem? item, string? id)
        {
            if (item == null)
                return null;

            if (item.ReleaseVariants is not { Count: > 0 } variants)
                return string.IsNullOrEmpty(id) ? item : null;

            // The default release (or no choice at all) is whatever the
            // item's own fields describe — no clone needed.
            if (string.IsNullOrEmpty(id))
                return item;

            var variant = variants.FirstOrDefault(v =>
                string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

            if (variant == null)
                return null;

            if (variant.IsDefault)
                return item;

            return new GalleryItem
            {
                Name = item.Name,
                Publisher = item.Publisher,
                Description = variant.Description ?? item.Description,
                ThumbnailUri = item.ThumbnailUri,
                DiskUri = variant.DiskUri,
                SymbolUri = item.SymbolUri,
                SecureBoot = item.SecureBoot,
                EnhancedSessionTransportType = item.EnhancedSessionTransportType,
                Version = variant.Version,
                LastUpdated = variant.LastUpdated ?? item.LastUpdated,
                InitialUsername = item.InitialUsername,
                InitialPassword = item.InitialPassword,
                LinuxDistro = item.LinuxDistro,
                // The chosen release may live in a different directory with
                // its own checksum file — prefer the variant's checksum.
                ChecksumUri = variant.ChecksumUri ?? item.ChecksumUri,
                Checksum = item.Checksum,
                ChecksumAlgorithm = item.ChecksumAlgorithm,
                Category = item.Category,
                IsRecommended = item.IsRecommended,
                IsNativeHyperV = item.IsNativeHyperV,
                SecureBootTemplate = item.SecureBootTemplate,
                Tags = item.Tags,
                IsWindows = item.IsWindows,
                // A variant may download from a filename whose extension does
                // not reveal the disk format (e.g. Rocky's .vhdfixed.xz
                // latest-aliases) — the loader's declared override must
                // survive resolution or the media-handler selection falls
                // back to extension inference and picks the wrong handler.
                FileTypeOverride = item.FileTypeOverride,
                ReleaseVariants = null,
            };
        }
    }
}
