using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class LoadFedoraSecurityLab : IGalleryLoader
    {
        private const string BaseUrl = "https://download.fedoraproject.org/pub/alt/releases/";
        private const string IsoPathTemplate = "{0}Labs/x86_64/iso/Fedora-Security-Live-x86_64-{1}-1.1.iso";
        private const string ReleaseVersion = "42";
        private const string Thumbnail = "https://fedoraproject.org/assets/images/fedora-security-logo.png";
        private const string LogoUri = "https://fedoraproject.org/assets/images/fedora-logo.png";
        private const string SymbolUri = "https://fedoraproject.org/assets/images/fedora-logo.png";

        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            // Construct the ISO URL with hardcoded version
            var isoUrl = string.Format(IsoPathTemplate, $"{BaseUrl}{ReleaseVersion}/", ReleaseVersion);
            var filename = $"Fedora-Security-Live-x86_64-{ReleaseVersion}-1.1.iso";

            // Use current UTC time as fallback for LastUpdated
            var lastModified = DateTime.UtcNow;

            var galleryItem = new GalleryItem
            {
                Name = "Fedora Security Lab",
                Publisher = "Fedora Project",
                Description = $"Fedora Security Lab, a live environment with tools for security research and penetration testing.",
                ThumbnailUri = Thumbnail,
                LogoUri = LogoUri,
                SymbolUri = SymbolUri,
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