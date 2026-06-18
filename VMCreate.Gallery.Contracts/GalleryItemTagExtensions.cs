using System;
using System.Linq;

namespace VMCreate
{
    /// <summary>
    /// Helper methods for working with gallery item tags.
    /// </summary>
    public static class GalleryItemTagExtensions
    {
        /// <summary>
        /// Returns true when the gallery item has the supplied tag (case-insensitive).
        /// Null or empty tags are treated as "not present".
        /// </summary>
        public static bool HasTag(this GalleryItem item, string tag)
        {
            if (item == null || string.IsNullOrEmpty(tag))
                return false;

            return item.Tags != null
                && item.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        }
    }
}
