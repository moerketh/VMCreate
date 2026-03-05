using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
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
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var galleryItems = new List<GalleryItem>();
            try
            {
                galleryItems = await _parser.LoadJsonFromUrl("https://go.microsoft.com/fwlink/?linkid=851584", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading gallery items from Microsoft URI: {ErrorMessage}", ex.Message);
            }
            return galleryItems;
        }
    }
}
