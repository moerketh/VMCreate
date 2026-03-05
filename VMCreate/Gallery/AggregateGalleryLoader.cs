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
            return results.SelectMany(r => r).ToList();
        }

        private async Task<List<GalleryItem>> LoadFromSingleLoader(IGalleryLoader loader, CancellationToken cancellationToken)
        {
            try
            {
                var items = await loader.LoadGalleryItems(cancellationToken);
                if (items == null)
                {
                    _logger.LogWarning("Loader {LoaderType} returned null items.", loader.GetType().Name);
                    return new List<GalleryItem>();
                }
                return items;
            }
            catch (OperationCanceledException)
            {
                throw; // Let cancellation propagate
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading items from {LoaderType}: {ErrorMessage}", loader.GetType().Name, ex.Message);
                return new List<GalleryItem>();
            }
        }
    }
}
