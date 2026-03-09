using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Orchestrates gallery loading, caching, and deduplication.
    /// </summary>
    public class GalleryService : IGalleryService
    {
        private readonly IGalleryCache _cache;
        private readonly IGalleryLoader _galleryLoader;
        private readonly ILogger<GalleryService> _logger;

        public GalleryService(
            IGalleryCache cache,
            IGalleryLoader galleryLoader,
            ILogger<GalleryService> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _galleryLoader = galleryLoader ?? throw new ArgumentNullException(nameof(galleryLoader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public List<GalleryItem> LoadFromCache()
        {
            if (_cache.TryLoadCache(out var items))
                return items;
            return new List<GalleryItem>();
        }

        public async Task LoadFromSourcesStreamingAsync(
            HashSet<(string Name, string DiskUri)> existingKeys,
            Action<List<GalleryItem>> onBatch,
            CancellationToken cancellationToken)
        {
            if (_galleryLoader is AggregateGalleryLoader aggregate)
            {
                await aggregate.LoadGalleryItemsStreaming(batch =>
                {
                    var newItems = batch
                        .Where(item => item.Name != null && item.DiskUri != null)
                        .ToList();
                    onBatch(newItems);
                }, cancellationToken);
            }
            else
            {
                var items = await _galleryLoader.LoadGalleryItems(cancellationToken);
                var newItems = items
                    .Where(i => i.Name != null && i.DiskUri != null)
                    .GroupBy(i => new { i.Name, i.DiskUri })
                    .Select(g => g.First())
                    .ToList();
                onBatch(newItems);
            }
        }

        public void SaveCache(List<GalleryItem> items)
        {
            _cache.SaveCache(items);
        }

    }
}
