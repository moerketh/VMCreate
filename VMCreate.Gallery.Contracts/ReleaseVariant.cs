using System;

namespace VMCreate
{
    /// <summary>
    /// One downloadable release of a gallery item (e.g. Kali's stable point
    /// release and its weekly builds). Items with more than one release show
    /// a release picker on the VM settings page; the chosen variant is
    /// resolved to an effective <see cref="GalleryItem"/> when deployment
    /// starts. Populated by gallery loaders from scrape results — unlike
    /// desktop options, these change with every upstream release.
    /// </summary>
    public sealed class ReleaseVariant
    {
        /// <summary>
        /// Stable machine id ("stable", "weekly"). Stored in wizard state
        /// and used as the CLI <c>--release</c> value. Must stay stable
        /// once shipped.
        /// </summary>
        public string Id { get; init; } = "";

        /// <summary>Human-readable label shown in the UI ("Stable (2026.2)").</summary>
        public string DisplayName { get; init; } = "";

        /// <summary>
        /// One-line description of the channel for the UI (e.g. "untested
        /// build with the latest updates"). May be null.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>The actual download for this release.</summary>
        public string DiskUri { get; init; } = "";

        /// <summary>Version string shown in the UI ("2026.2", "2026-W26").</summary>
        public string Version { get; init; } = "";

        /// <summary>Release date string as displayed in the UI. May be null.</summary>
        public string? LastUpdated { get; init; }

        /// <summary>Download size as scraped from the source ("2.4G"). Advisory only.</summary>
        public string? DownloadSizeText { get; init; }

        /// <summary>
        /// True when this is the release the gallery item itself describes.
        /// Exactly one variant per item must be the default; choosing the
        /// default (or nothing at all) deploys the item's own fields
        /// unchanged.
        /// </summary>
        public bool IsDefault { get; init; }
    }
}
