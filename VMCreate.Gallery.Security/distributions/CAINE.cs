using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for CAINE (Computer Aided INvestigative Environment) — an Italian
    /// Ubuntu-based digital forensics and incident response (DFIR) distribution.
    /// Update <see cref="PinnedVersion"/> and <see cref="IsoUrl"/> on each new release.
    /// </summary>
    public class CAINE : IGalleryLoader
    {
        private const string PinnedVersion = "14.0";
        private const string IsoUrl = "https://www.caine-live.net/Downloads/caine14.0.iso";
        private const string? PublicLogoUrl = "https://www.caine-live.net/resources/images/caine.png";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(PublicLogoUrl, typeof(CAINE).Assembly, "caine-logo.svg");
            var item = new GalleryItem
            {
                Name        = "CAINE",
                Publisher   = "CAINE Team",
                Description = $"Computer Aided INvestigative Environment — a comprehensive Italian digital forensics and incident response (DFIR) distribution based on Ubuntu. Integrates seamlessly with Windows investigations (version {PinnedVersion}).",
                ThumbnailUri = logoUri,
                LogoUri      = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = IsoUrl,
                ArchiveRelativePath = "",
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = PinnedVersion,
                LastUpdated  = DateTime.UtcNow.ToString("o"),
                Category     = "Security"
            };
            return new List<GalleryItem> { item };
        }
    }
}
