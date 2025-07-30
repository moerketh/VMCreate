using CreateVM;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public  class LoadFromUbuntuGitHub : IGalleryLoader
    {
        const string UbuntuNobleGalleryURI = "https://raw.githubusercontent.com/canonical/ubuntu-desktop-hyper-v/master/HyperVGallery/Ubuntu-24.04.xml";
        private readonly ILogger<LoadFromUbuntuGitHub> _logger;
        private readonly IGalleryItemsParser _parser;

        public LoadFromUbuntuGitHub(ILogger<LoadFromUbuntuGitHub> logger, IGalleryItemsParser parser)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var galleryItems = new List<GalleryItem>();
            var items = await _parser.LoadXmlFromUrl(UbuntuNobleGalleryURI);
            return items;
        }
    }
}
