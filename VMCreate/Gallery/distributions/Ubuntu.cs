using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public  class Ubuntu : IGalleryLoader
    {
        //const string UbuntuNobleGalleryURI = "https://raw.githubusercontent.com/canonical/ubuntu-desktop-hyper-v/master/HyperVGallery/Ubuntu-24.04.xml";
        const string BaseUri = "https://partner-images.canonical.com/hyper-v/desktop/";
        const string JammyCurrent = BaseUri + "jammy/release/current/ubuntu-jammy-hyperv-amd64-ubuntu-desktop-hyperv.vhdx.zip";
        const string NobleCurrent = BaseUri + "noble/release/current/ubuntu-noble-hyperv-amd64-ubuntu-desktop-hyperv.vhdx.zip";
        const string OracularCurrent= BaseUri + "oracular/release/current/ubuntu-oracular-hyperv-amd64-ubuntu-desktop-hyperv.vhdx.zip";
        const string ThumbnailUri = BaseUri + "/bionic/ubuntu_thumbnail.jpg";
        const string SymbolUri = BaseUri + "bionic/ubuntu_symbol.png";
        const string LogoUri = BaseUri + "bionic/ubuntu_logo.png";
        private readonly ILogger<Ubuntu> _logger;
        private readonly IHttpClientFactory _clientFactory;

        
        public Ubuntu(ILogger<Ubuntu> logger, IHttpClientFactory clientFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var galleryItems = new List<GalleryItem>();
            var jammyGalleryItem = new GalleryItem
            {
                Name = "Ubuntu 22.04 LTS",
                Publisher = "Canonical Group Ltd",
                Description = $"The open source desktop operating system that powers millions of PCs and laptops around the world.",
                ThumbnailUri = ThumbnailUri,
                SymbolUri = SymbolUri,
                LogoUri = LogoUri,
                DiskUri = JammyCurrent,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = "22.04 LTS",
                LastUpdated = await ParseBuildInfo(BaseUri + "jammy/current/unpacked/build-info.txt", cancellationToken)
            };
            galleryItems.Add(jammyGalleryItem);
            var nobleGalleryItem = new GalleryItem
            {
                Name = "Ubuntu 24.04 LTS",
                Publisher = "Canonical Group Ltd",
                Description = $"The open source desktop operating system that powers millions of PCs and laptops around the world.",
                ThumbnailUri = ThumbnailUri,
                SymbolUri = SymbolUri,
                LogoUri = LogoUri,
                DiskUri = NobleCurrent,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = "24.04 LTS",
                LastUpdated = await ParseBuildInfo(BaseUri + "noble/current/unpacked/build-info.txt", cancellationToken)
            };
            galleryItems.Add(nobleGalleryItem);
            var oracularGalleryItem = new GalleryItem
            {
                Name = "Ubuntu 24.10",
                Publisher = "Canonical Group Ltd",
                Description = $"The open source desktop operating system that powers millions of PCs and laptops around the world.",
                ThumbnailUri = ThumbnailUri,
                SymbolUri = SymbolUri,
                LogoUri = LogoUri,
                DiskUri = OracularCurrent,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = "24.10",
                LastUpdated = await ParseBuildInfo(BaseUri + "oracular/current/unpacked/build-info.txt", cancellationToken)
            };
            galleryItems.Add(oracularGalleryItem);
            return galleryItems;
        }

        private async Task<string> ParseBuildInfo(string buildInfoUri, CancellationToken cancellationToken = default)
        {
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

            var response = await client.GetAsync(buildInfoUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var splt = content.Split('=');
            if (splt.Length > 1)
            {
                return splt[1].Trim();
            }
            return null;
        }
    }
}
