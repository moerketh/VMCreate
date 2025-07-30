using CreateVM;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Management.Automation.Language;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class LoadFromMicrosoftURI : IGalleryLoader
    {
        private readonly ILogger<LoadFromMicrosoftURI> _logger;
        private readonly IGalleryItemsParser _parser;

        public LoadFromMicrosoftURI(ILogger<LoadFromMicrosoftURI> logger, IGalleryItemsParser parser)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        }

        /// <summary>
        /// Load Microsoft Default Hyper-V Gallery Items from URI
        /// </summary>
        /// <returns></returns>
        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var galleryItems = new List<GalleryItem>();
            try
            {
                // Load Microsoft Default gallery items
                galleryItems = await _parser.LoadJsonFromUrl("https://go.microsoft.com/fwlink/?linkid=851584");
            }
            catch (Exception ex)
            {
                // Handle exceptions, e.g., log the error
                _logger.LogError($"Error loading gallery items from Microsoft URI: {ex.Message}");
            }
            return galleryItems;
        }
    }
}
