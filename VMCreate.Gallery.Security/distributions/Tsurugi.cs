using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for Tsurugi Linux — an Italian DFIR and OSINT distribution.
    /// Update <see cref="PinnedVersion"/> and <see cref="IsoUrl"/> on each new release
    /// published at https://tsurugi-linux.org.
    /// </summary>
    public class Tsurugi : IGalleryLoader
    {
        private const string PinnedVersion = "25.11";
        private const string IsoUrl = "https://ftp.nluug.nl/os/Linux/distr/tsurugi/01.Tsurugi_Linux_%5bLAB%5d/tsurugi_linux_25.11.iso";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(Tsurugi).Assembly, "tsurugi-logo.svg");
            var item = new GalleryItem
            {
                Name        = "Tsurugi Linux",
                Publisher   = "Tsurugi Linux Team",
                Description = $"Tsurugi Linux is an Italian DFIR (Digital Forensics and Incident Response) and OSINT distribution focused on open-source investigation, providing a wide collection of pre-installed forensics and intelligence-gathering tools (version {PinnedVersion}).",
                ThumbnailUri = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = IsoUrl,
                EnhancedSessionTransportType = "HvSocket",
                Version      = PinnedVersion,
                LastUpdated  = DateTime.UtcNow.ToString("o"),
                Category     = "Security"
            };
            return new List<GalleryItem> { item };
        }
    }
}
