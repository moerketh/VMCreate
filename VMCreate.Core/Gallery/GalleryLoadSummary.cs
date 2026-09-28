using System.Collections.Generic;
using System.Linq;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Outcome metadata of a gallery load, for status-line surfacing:
    /// how many loaders ran and which ones failed, so the UI can show
    /// "N images · live from distribution sites · M loaders failed"
    /// instead of silently displaying fewer cards than the catalog has.
    /// </summary>
    public sealed class GalleryLoadSummary
    {
        public int LoaderCount { get; init; }

        /// <summary>Names of loaders that failed or returned nothing.</summary>
        public IReadOnlyList<string> FailedLoaders { get; init; } = new List<string>();

        public static GalleryLoadSummary Empty { get; } = new();

        /// <summary>
        /// Composes the user-facing status line: item count, source, and —
        /// when loaders failed — the failure count with the names as tooltip.
        /// Pure function so it is unit-testable without WPF.
        /// </summary>
        public static (string Text, string? ToolTip) ComposeStatusLine(int itemCount, GalleryLoadSummary summary)
        {
            string text = $"{itemCount} images · live from distribution sites";
            string? toolTip = null;
            if (summary.FailedLoaders.Count > 0)
            {
                text += $" · {summary.FailedLoaders.Count} of {summary.LoaderCount} loaders failed";
                toolTip = "Images from these sources are missing:\n" + string.Join("\n", summary.FailedLoaders);
            }
            return (text, toolTip);
        }
    }
}

