using CreateVM;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class LoadFromRegistry : IGalleryLoader
    {
        private readonly ILogger<LoadFromRegistry> _logger;
        private readonly IGalleryItemsParser _parser;

        public LoadFromRegistry(ILogger<LoadFromRegistry> logger, IGalleryItemsParser parser)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        }
        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var items = new List<GalleryItem>();
            try
            {
                string registryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
                string[] locations = null;
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(registryPath))
                {
                    if (key != null)
                    {
                        locations = key.GetValue("GalleryLocations") as string[];
                    }
                }
                if (locations == null || locations.Length == 0)
                {
                    _logger.LogWarning("GalleryLocations registry key not found or empty.");
                }
                else
                {
                    _logger.LogDebug($"Found GalleryLocations: {string.Join(", ", locations)}");
                    foreach (string location in locations)
                    {
                        if (location.StartsWith("http"))
                        {
                            items = await _parser.LoadJsonFromUrl(location);
                        }
                        else if (Directory.Exists(location))
                        {
                            items = _parser.LoadJsonFromFiles(location);
                        }
                        else
                        {
                            _logger.LogError($"Invalid local path: {location}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading gallery items from registry: {ex.Message}");
            }
            return items;
        }
    }
}

