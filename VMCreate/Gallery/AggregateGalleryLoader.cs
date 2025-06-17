using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class AggregateGalleryLoader : IGalleryLoader
    {
        private readonly List<IGalleryLoader> _loaders;
        private readonly IServiceProvider _serviceProvider;

        public AggregateGalleryLoader(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _loaders = new List<IGalleryLoader>();

            // Read enabled gallery loaders from App.config
            var enabledLoadersConfig = ConfigurationManager.AppSettings["EnabledGalleryLoaders"];
            if (string.IsNullOrWhiteSpace(enabledLoadersConfig))
            {
                Console.WriteLine("Warning: No gallery loaders configured in App.config.");
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
                        Console.WriteLine($"Error: Could not find type '{typeName}'. Verify the namespace and assembly.");
                        continue;
                    }

                    if (!typeof(IGalleryLoader).IsAssignableFrom(type))
                    {
                        Console.WriteLine($"Error: Type '{typeName}' does not implement IGalleryLoader.");
                        continue;
                    }

                    var loader = _serviceProvider.GetService(type) as IGalleryLoader;
                    if (loader == null)
                    {
                        Console.WriteLine($"Error: Could not resolve loader for type '{typeName}'. Ensure it is registered in the DI container.");
                        continue;
                    }

                    _loaders.Add(loader);
                    Console.WriteLine($"Successfully loaded loader: {typeName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error resolving loader '{typeName}': {ex.Message}");
                }
            }

            if (!_loaders.Any())
            {
                Console.WriteLine("Warning: No valid gallery loaders were loaded.");
            }
        }

        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var allItems = new List<GalleryItem>();
            foreach (var loader in _loaders)
            {
                try
                {
                    var items = await loader.LoadGalleryItems();
                    if (items != null)
                    {
                        allItems.AddRange(items);
                    }
                    else
                    {
                        Console.WriteLine($"Warning: Loader {loader.GetType().Name} returned null items.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading items from {loader.GetType().Name}: {ex.Message}");
                }
            }
            return allItems;
        }
    }
}