using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class Arch : IGalleryLoader
    {
        private const string TemplateUrl = "https://gitlab.archlinux.org/archlinux/arch-boxes/-/package_files/9907/download";
        private const string Thumbnail = "https://fedoraproject.org/assets/images/fedora-silverblue-logo.png";
        private const string LogoUri = "https://fedoraproject.org/assets/images/fedora-logo.png";
        private const string SymbolUri = "https://fedoraproject.org/assets/images/fedora-logo.png";

        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            // Use current UTC time as fallback for LastUpdated
            var lastModified = DateTime.UtcNow;

            var galleryItem = new GalleryItem
            {
                Name = "Arch",
                Publisher = "Arch Linux",
                Description = "A lightweight and flexible Linux® distribution that tries to Keep It Simple.",
                ThumbnailUri = Thumbnail,
                LogoUri = LogoUri,
                SymbolUri = SymbolUri,
                DiskUri = TemplateUrl,
                ArchiveRelativePath = "",
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                LastUpdated = lastModified.ToString("o")
            };
            return new List<GalleryItem> { galleryItem };
        }
    }
}