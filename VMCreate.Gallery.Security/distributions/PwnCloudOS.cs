using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery.distributions
{
    public class PwnCloudOS : IGalleryLoader
    {
        private const string TemplateUrl = "https://download.pwncloudos.pwnedlabs.io/images/pwncloudos-amd64.ova";
        private const string SymbolUri = "https://pwncloudos.pwnedlabs.io/hubfs/pwnedlabs-notagline.svg";
        private const string ThumbnailUri = "https://pwncloudos.pwnedlabs.io/hubfs/image-1.png";

        public Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var lastModified = DateTime.UtcNow;

            var galleryItem = new GalleryItem
            {
                Name = "PwnCloudOS",
                Publisher = "Pwned Labs",
                Description = $"The multi-cloud security platform for hackers and defenders.",
                SymbolUri = SymbolUri,
                ThumbnailUri = ThumbnailUri,
                DiskUri = TemplateUrl,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                LastUpdated = lastModified.ToString("o"),
                InitialUsername = "pwnedlabs",
                InitialPassword = "pwnedlabs",
                Category = "Security",
                IsRecommended = true
            };
            return Task.FromResult(new List<GalleryItem> { galleryItem });
        }
    }
}