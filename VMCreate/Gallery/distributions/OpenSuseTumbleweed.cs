using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for openSUSE Tumbleweed rolling release (KDE Plasma Live ISO).
    /// Uses the stable "-Current" redirect URL maintained by the openSUSE mirrors —
    /// always resolves to the latest KDE Live build without requiring code changes.
    /// The KDE Live spin ships a graphical Wayland Plasma session, which is required
    /// for the Lamco RDP Server backend (Lamco shares an existing Wayland session).
    /// </summary>
    public class OpenSuseTumbleweed : IGalleryLoader
    {
        // This URL 302-redirects to the latest Tumbleweed KDE Live ISO.
        private const string IsoUrl = "https://download.opensuse.org/tumbleweed/iso/openSUSE-Tumbleweed-KDE-Live-x86_64-Current.iso";
        private const string ChecksumUrl = IsoUrl + ".sha256";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(OpenSuseTumbleweed).Assembly, "opensuse-logo.svg");
            var item = new GalleryItem
            {
                Name        = "openSUSE Tumbleweed (KDE)",
                Publisher   = "SUSE / openSUSE Project",
                Description = "openSUSE Tumbleweed is a rolling-release Linux distribution delivering the latest stable kernel, libraries and desktop environments. The KDE Live ISO ships a Wayland-native Plasma session, suitable for the Lamco RDP Server backend.",
                ThumbnailUri = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = IsoUrl,
                ChecksumUri  = ChecksumUrl,
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = "Tumbleweed",
                LastUpdated  = DateTime.UtcNow.ToString("o"),
                LinuxDistro  = LinuxDistro.OpenSuse
            };
            return new List<GalleryItem> { item };
        }
    }
}
