using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// High-level service that orchestrates gallery loading, caching, deduplication,
    /// and profile photo resolution. Extracts business logic that was previously in MainWindow.
    /// </summary>
    public interface IGalleryService
    {
        /// <summary>
        /// Loads gallery items from cache (if fresh) and returns them.
        /// Returns an empty list if cache is stale or missing.
        /// </summary>
        List<GalleryItem> LoadFromCache();

        /// <summary>
        /// Loads gallery items from all sources, streaming batches via the callback as each source completes.
        /// Deduplicates against items already known via <paramref name="existingKeys"/>.
        /// </summary>
        Task LoadFromSourcesStreamingAsync(
            HashSet<(string Name, string DiskUri)> existingKeys,
            Action<List<GalleryItem>> onBatch,
            CancellationToken cancellationToken);

        /// <summary>
        /// Persists the given gallery items to the local cache.
        /// </summary>
        void SaveCache(List<GalleryItem> items);


    }
}
