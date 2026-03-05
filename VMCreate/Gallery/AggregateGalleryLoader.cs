using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class AggregateGalleryLoader : IGalleryLoader
    {
        private readonly List<IGalleryLoader> _loaders;
        private readonly ILogger<AggregateGalleryLoader> _logger;
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Testable constructor — supply the loaders directly without App.config or DI.
        /// </summary>
        public AggregateGalleryLoader(ILogger<AggregateGalleryLoader> logger, IEnumerable<IGalleryLoader> loaders)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loaders = (loaders ?? throw new ArgumentNullException(nameof(loaders))).ToList();
        }

        public AggregateGalleryLoader(ILogger<AggregateGalleryLoader> logger, IServiceProvider serviceProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _loaders = new List<IGalleryLoader>();

            // Read enabled gallery loaders from App.config
            var enabledLoadersConfig = ConfigurationManager.AppSettings["EnabledGalleryLoaders"];
            if (string.IsNullOrWhiteSpace(enabledLoadersConfig))
            {
                _logger.LogCritical("Warning: No gallery loaders configured in App.config.");
                return;
            }

            var enabledLoaderTypes = enabledLoadersConfig.Split(',')
                .Select(typeName => typeName.Trim())
                .Where(typeName => !string.IsNullOrEmpty(typeName))
                .ToList();

            // Resolve and add enabled loaders
            foreach (var typeName in enabledLoaderTypes)
            {
                try
                {
                    var type = Type.GetType(typeName);
                    if (type == null)
                    {
                        _logger.LogError($"Error: Could not find type '{typeName}'. Verify the namespace and assembly.");
                        continue;
                    }

                    if (!typeof(IGalleryLoader).IsAssignableFrom(type))
                    {
                        _logger.LogError($"Error: Type '{typeName}' does not implement IGalleryLoader.");
                        continue;
                    }

                    var loader = _serviceProvider.GetService(type) as IGalleryLoader;
                    if (loader == null)
                    {
                        _logger.LogError($"Error: Could not resolve loader for type '{typeName}'. Ensure it is registered in the DI container.");
                        continue;
                    }

                    _loaders.Add(loader);
                    _logger.LogDebug($"Successfully loaded loader: {typeName}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error resolving loader '{typeName}': {ex.Message}");
                }
            }

            if (!_loaders.Any())
            {
                _logger.LogCritical("Warning: No valid gallery loaders were loaded.");
            }
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
                    _logger.LogWarning($"Warning: Loader {loader.GetType().Name} returned null items.");
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
                _logger.LogError($"Error loading items from {loader.GetType().Name}: {ex.Message}");
                return new List<GalleryItem>();
            }
        }
    }
}
