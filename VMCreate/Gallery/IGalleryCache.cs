using System.Collections.Generic;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Persists gallery items to a local cache so that subsequent app launches
    /// can display results instantly while a background refresh runs.
    /// </summary>
    public interface IGalleryCache
    {
        /// <summary>
        /// Attempts to load cached gallery items from disk.
        /// Returns true when the cache exists and is fresh.
        /// </summary>
        bool TryLoadCache(out List<GalleryItem> items);

        /// <summary>
        /// Persists the given gallery items to the local cache file.
        /// </summary>
        void SaveCache(List<GalleryItem> items);
    }
}
