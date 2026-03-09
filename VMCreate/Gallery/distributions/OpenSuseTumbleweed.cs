using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for openSUSE Tumbleweed rolling release.
    /// Uses the stable "-Current" redirect URL maintained by the openSUSE mirrors —
    /// always resolves to the latest NET installer build without requiring code changes.
    /// </summary>
    public class OpenSuseTumbleweed : IGalleryLoader
    {
        // This URL 302-redirects to the latest Tumbleweed NET installer ISO.
        private const string IsoUrl = "https://download.opensuse.org/tumbleweed/iso/openSUSE-Tumbleweed-NET-x86_64-Current.iso";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(OpenSuseTumbleweed).Assembly, "opensuse-logo.svg");
            var item = new GalleryItem
            {
                Name        = "openSUSE Tumbleweed",
                Publisher   = "SUSE / openSUSE Project",
                Description = "openSUSE Tumbleweed is a rolling-release Linux distribution delivering the latest stable kernel, libraries and desktop environments. The NET installer fetches packages at setup time, ensuring a fully up-to-date system.",
                ThumbnailUri = logoUri,
                LogoUri      = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = IsoUrl,
                ArchiveRelativePath = "",
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = "Tumbleweed",
                LastUpdated  = DateTime.UtcNow.ToString("o")
            };
            return new List<GalleryItem> { item };
        }
    }
}
