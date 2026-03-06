using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Persists gallery items to a local JSON file so that subsequent app launches
    /// can display results instantly while a background refresh runs.
    /// </summary>
    public class GalleryCache
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        private readonly string _cacheFilePath;
        private readonly ILogger<GalleryCache> _logger;

        public GalleryCache(ILogger<GalleryCache> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheFilePath = Path.Combine(appData, "VMCreate", "gallery-cache.json");
        }

        /// <summary>
        /// Attempts to load cached gallery items from disk.
        /// Returns <c>true</c> when the cache exists and is younger than <see cref="CacheTtl"/>.
        /// </summary>
        public bool TryLoadCache(out List<GalleryItem> items)
        {
            items = null;

            try
            {
                if (!File.Exists(_cacheFilePath))
                    return false;

                var fileInfo = new FileInfo(_cacheFilePath);
                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > CacheTtl)
                {
                    _logger.LogDebug("Gallery cache expired (age {Age}).", DateTime.UtcNow - fileInfo.LastWriteTimeUtc);
                    return false;
                }

                var json = File.ReadAllText(_cacheFilePath);
                items = JsonSerializer.Deserialize<List<GalleryItem>>(json, JsonOptions);

                if (items == null || items.Count == 0)
                {
                    _logger.LogDebug("Gallery cache was empty or invalid.");
                    return false;
                }

                _logger.LogInformation("Loaded {Count} gallery items from cache.", items.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read gallery cache; will fetch from network.");
                return false;
            }
        }

        /// <summary>
        /// Persists the given gallery items to the local cache file.
        /// Writes to a temporary file first, then atomically moves it into place.
        /// </summary>
        public void SaveCache(List<GalleryItem> items)
        {
            if (items == null || items.Count == 0)
                return;

            try
            {
                var directory = Path.GetDirectoryName(_cacheFilePath);
                Directory.CreateDirectory(directory);

                var tempPath = _cacheFilePath + ".tmp";
                var json = JsonSerializer.Serialize(items, JsonOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _cacheFilePath, overwrite: true);

                _logger.LogInformation("Saved {Count} gallery items to cache.", items.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write gallery cache.");
            }
        }
    }
}
