using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class LoadFromLocalJsonFile : IGalleryLoader
    {
        private readonly ILogger<LoadFromLocalJsonFile> _logger;
        private readonly IGalleryItemsParser _parser;

        public LoadFromLocalJsonFile(ILogger<LoadFromLocalJsonFile> logger, IGalleryItemsParser parser)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        }

        /// <summary>
        /// Load a local gallery.json file
        /// </summary>
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var items = new List<GalleryItem>();
            string localJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gallery.json");
            if (File.Exists(localJsonPath))
            {
                _logger.LogDebug($"Loading local JSON from: {localJsonPath}");
                items = _parser.LoadJsonFromFile(localJsonPath);
            }
            else
            {
                _logger.LogWarning($"Local JSON file not found: {localJsonPath}");
            }
            return items;
        }
    }
}
