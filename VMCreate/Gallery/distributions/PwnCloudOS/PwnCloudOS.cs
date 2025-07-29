using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class PwnCloudOS : IGalleryLoader
    {
        private const string TemplateUrl = "https://download.pwncloudos.pwnedlabs.io/images/pwncloudos-amd64.ova";
        private const string LogoUri = "https://pwncloudos.pwnedlabs.io/hubfs/pwnedlabs-notagline.svg";
        private const string SymbolUri = "https://pwncloudos.pwnedlabs.io/hubfs/favicon-1.svg";

        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            // Use current UTC time as fallback for LastUpdated
            var lastModified = DateTime.UtcNow;

            var galleryItem = new GalleryItem
            {
                Name = "PwnCloudOS",
                Publisher = "Pwned Labs",
                Description = $"The multi-cloud security platform for hackers and defenders.",
                LogoUri = LogoUri,
                SymbolUri = SymbolUri,
                DiskUri = TemplateUrl,
                ArchiveRelativePath = "amd64_pwncloudosv1.2-disk1.vmdk",
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                LastUpdated = lastModified.ToString("o"),
                InitialUsername = "pwnedlabs",
                InitialPassword = "pwnedlabs"
            };
            return new List<GalleryItem> { galleryItem };
        }
    }
}