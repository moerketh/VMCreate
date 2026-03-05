using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class FedoraSilverblue : IGalleryLoader
    {
        private const string BaseUrl = "https://download.fedoraproject.org/pub/fedora/linux/releases/";
        private const string IsoPathTemplate = "{0}Silverblue/x86_64/iso/Fedora-Silverblue-ostree-x86_64-{1}-1.1.iso";
        private const string ReleaseVersion = "42";
        private const string? PublicLogoUrl = "https://fedoraproject.org/assets/images/fedora-logo.png";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(PublicLogoUrl, typeof(FedoraSilverblue).Assembly, "fedora-logo.svg");
            // Construct the ISO URL with hardcoded version
            var isoUrl = string.Format(IsoPathTemplate, $"{BaseUrl}{ReleaseVersion}/", ReleaseVersion);
            var filename = $"Fedora-Silverblue-ostree-x86_64-{ReleaseVersion}-1.1.iso";

            // Use current UTC time as fallback for LastUpdated
            var lastModified = DateTime.UtcNow;

            var galleryItem = new GalleryItem
            {
                Name = "Fedora Silverblue",
                Publisher = "Fedora Project",
                Description = $"Fedora Silverblue, an immutable desktop OS with GNOME for a reliable and modern user experience.",
                ThumbnailUri = logoUri,
                LogoUri = logoUri,
                SymbolUri = logoUri,
                DiskUri = isoUrl,
                ArchiveRelativePath = filename,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = ReleaseVersion,
                LastUpdated = lastModified.ToString("o")
            };
            return new List<GalleryItem> { galleryItem };
        }
    }
}