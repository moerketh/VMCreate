using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class Arch : IGalleryLoader
    {
        private const string TemplateUrl = "https://geo.mirror.pkgbuild.com/images/latest/Arch-Linux-x86_64-basic.qcow2";
        private const string Thumbnail = "";
        private const string LogoUri = "";
        private const string SymbolUri = "https://archlinux.org/static/favicon.png";

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
                LastUpdated = lastModified.ToString("o"),
                InitialUsername = "arch",
                InitialPassword = "arch"
            };
            return new List<GalleryItem> { galleryItem };
        }
    }
}