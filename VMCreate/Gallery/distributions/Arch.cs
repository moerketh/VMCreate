using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class Arch : IGalleryLoader
    {
        private const string TemplateUrl = "https://geo.mirror.pkgbuild.com/images/latest/Arch-Linux-x86_64-basic.qcow2";
        private const string Thumbnail = "";
        private const string LogoUri = "";
        private const string SymbolUri = "";

        public Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
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
                LastUpdated = DateTime.UtcNow.ToString("o"),
                InitialUsername = "arch",
                InitialPassword = "arch"
            };
            return Task.FromResult(new List<GalleryItem> { galleryItem });
        }
    }
}