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
        private const string PinnedVersion = "2024.1";
        private const string IsoUrl = "https://tsurugi-linux.org/downloads/tsurugi_2024.1_amd64.iso";
        private const string? PublicLogoUrl = "https://tsurugi-linux.org/images/logo.png";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(PublicLogoUrl, typeof(Tsurugi).Assembly, "tsurugi-logo.svg");
            var item = new GalleryItem
            {
                Name        = "Tsurugi Linux",
                Publisher   = "Tsurugi Linux Team",
                Description = $"Tsurugi Linux is an Italian DFIR (Digital Forensics and Incident Response) and OSINT distribution focused on open-source investigation, providing a wide collection of pre-installed forensics and intelligence-gathering tools (version {PinnedVersion}).",
                ThumbnailUri = logoUri,
                LogoUri      = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = IsoUrl,
                ArchiveRelativePath = "",
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = PinnedVersion,
                LastUpdated  = DateTime.UtcNow.ToString("o")
            };
            return new List<GalleryItem> { item };
        }
    }
}
