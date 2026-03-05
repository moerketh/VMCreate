using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for Linux Mint (Cinnamon edition), pinned to the current stable release.
    /// Update <see cref="PinnedVersion"/> and <see cref="IsoUrl"/> when a new stable release.
    /// is published at https://www.linuxmint.com/download.php.
    /// </summary>
    public class LinuxMint : IGalleryLoader
    {
        private const string PinnedVersion = "22.1";
        private const string IsoUrl = "https://mirrors.edge.kernel.org/linuxmint/stable/22.1/linuxmint-22.1-cinnamon-64bit.iso";
        private const string? PublicLogoUrl = "https://www.linuxmint.com/pictures/logo.png";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(PublicLogoUrl, typeof(LinuxMint).Assembly, "linuxmint-logo.svg");
            var item = new GalleryItem
            {
                Name        = $"Linux Mint {PinnedVersion}",
                Publisher   = "Linux Mint Project",
                Description = $"Linux Mint is a user-friendly, Ubuntu-based distribution featuring the Cinnamon desktop. Ideal for users migrating from Windows, it emphasises ease of use, stability and out-of-the-box multimedia support (version {PinnedVersion}).",
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
