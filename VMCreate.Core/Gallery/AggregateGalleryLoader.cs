using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class AggregateGalleryLoader : IGalleryLoader
    {
        private readonly List<IGalleryLoader> _loaders;
        private readonly ILogger<AggregateGalleryLoader> _logger;

        public AggregateGalleryLoader(ILogger<AggregateGalleryLoader> logger, IEnumerable<IGalleryLoader> loaders)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loaders = (loaders ?? throw new ArgumentNullException(nameof(loaders))).ToList();

            if (!_loaders.Any())
                _logger.LogCritical("Warning: No valid gallery loaders were loaded.");
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            // Run all loaders in parallel; individual failures are isolated and logged
            var tasks = _loaders.Select(loader => LoadFromSingleLoader(loader, cancellationToken));
            var results = await Task.WhenAll(tasks);
            return DeduplicateByDiskUri(results.SelectMany(r => r));
        }

        /// <summary>
        /// Multiple loaders may return items with the same (Name, DiskUri) pair
        /// (e.g. the Microsoft gallery URL appears both in LoadFromMicrosoftURI and
        /// in the registry GalleryLocations). Keep only the first occurrence so the
        /// UI doesn't show exact duplicates. Items that share a DiskUri but have
        /// different Names (e.g. "FLARE VM" and "Windows 11 dev environment") are
        /// distinct products and both are kept.
        /// </summary>
        private List<GalleryItem> DeduplicateByDiskUri(IEnumerable<GalleryItem> items)
        {
            var seen = new HashSet<(string, string)>();
            var result = new List<GalleryItem>();
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.DiskUri) || string.IsNullOrEmpty(item.Name))
                {
                    result.Add(item);
                    continue;
                }

                if (seen.Add((item.Name, item.DiskUri)))
                    result.Add(item);
                else
                    _logger.LogDebug("Skipping duplicate gallery item: {Name} ({DiskUri})", item.Name, item.DiskUri);
            }
            return result;
        }

        /// <summary>
        /// Like <see cref="LoadGalleryItems"/> but invokes <paramref name="onBatch"/> each time
        /// any individual loader finishes, allowing the caller to stream results into the UI
        /// progressively instead of waiting for all loaders to complete.
        /// </summary>
        public async Task LoadGalleryItemsStreaming(
            Action<List<GalleryItem>> onBatch,
            CancellationToken cancellationToken = default)
        {
            var seenKeys = new HashSet<(string, string)>();
            var pending = _loaders
                .Select(loader => LoadFromSingleLoader(loader, cancellationToken))
                .ToList();

            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                var items = await completed;
                // Deduplicate by (Name, DiskUri) — items with the same DiskUri but
                // different Names (e.g. "FLARE VM" vs "Windows 11 dev environment")
                // are distinct products and both are shown.
                var unique = items
                    .Where(i => i.Name != null && i.DiskUri != null
                        && seenKeys.Add((i.Name, i.DiskUri)))
                    .ToList();
                if (unique.Count > 0)
                    onBatch(unique);
            }
        }

        /// <summary>Maximum time any single loader is allowed before it is abandoned.</summary>
        private static readonly TimeSpan LoaderTimeout = TimeSpan.FromSeconds(10);

        private async Task<List<GalleryItem>> LoadFromSingleLoader(IGalleryLoader loader, CancellationToken cancellationToken)
        {
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.CancelAfter(LoaderTimeout);

                var items = await loader.LoadGalleryItems(linkedCts.Token);
                if (items == null)
                {
                    _logger.LogWarning("Loader {LoaderType} returned null items.", loader.GetType().Name);
                    return new List<GalleryItem>();
                }
                return items;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The per-loader timeout fired, not the caller's token — treat as a soft failure.
                _logger.LogWarning("Loader {LoaderType} timed out after {Timeout}s and was skipped.",
                    loader.GetType().Name, LoaderTimeout.TotalSeconds);
                return new List<GalleryItem>();
            }
            catch (OperationCanceledException)
            {
                throw; // Caller requested cancellation — propagate.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading items from {LoaderType}: {ErrorMessage}", loader.GetType().Name, ex.Message);
                return new List<GalleryItem>();
            }
        }
    }
}
