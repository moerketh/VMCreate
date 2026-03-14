using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for the Fedora Workstation Live ISO.
    /// Same URL pattern as <see cref="FedoraSilverblue"/> — update
    /// <see cref="ReleaseVersion"/> each Fedora release cycle (~6 months).
    /// </summary>
    public class FedoraWorkstation : IGalleryLoader
    {
        private const string BaseUrl         = "https://download.fedoraproject.org/pub/fedora/linux/releases/";
        private const string IsoPathTemplate = "{0}Workstation/x86_64/iso/Fedora-Workstation-Live-{1}-1.1.x86_64.iso";
        private const string ChecksumTemplate = "{0}Workstation/x86_64/iso/Fedora-Workstation-{1}-1.1-x86_64-CHECKSUM";
        private const string ReleaseVersion  = "42";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(FedoraWorkstation).Assembly, "fedora-logo.svg");
            var isoUrl   = string.Format(IsoPathTemplate, $"{BaseUrl}{ReleaseVersion}/", ReleaseVersion);
            var checksumUrl = string.Format(ChecksumTemplate, $"{BaseUrl}{ReleaseVersion}/", ReleaseVersion);
            var filename = $"Fedora-Workstation-Live-{ReleaseVersion}-1.1.x86_64.iso";

            var item = new GalleryItem
            {
                Name        = $"Fedora Workstation {ReleaseVersion}",
                Publisher   = "Fedora Project",
                Description = $"Fedora Workstation is the flagship desktop edition of Fedora, delivering the latest GNOME experience on a modern, cutting-edge operating system base. Ideal for developers and power users (version {ReleaseVersion}).",
                ThumbnailUri = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = isoUrl,
                ChecksumUri  = checksumUrl,
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = ReleaseVersion,
                LastUpdated  = DateTime.UtcNow.ToString("o")
            };
            return new List<GalleryItem> { item };
        }
    }
}
