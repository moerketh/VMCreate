using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for the REMnux malware-analysis OVA (Ubuntu-based).
    /// URL is pinned; update <see cref="PinnedVersion"/> and <see cref="OvaUrl"/>
    /// when a new major release is published at https://docs.remnux.org.
    /// </summary>
    public class REMnux : IGalleryLoader
    {
        private const string PinnedVersion = "noble-202602";
        private const string OvaUrl = "https://download.remnux.org/202602/remnux-noble-amd64.ova";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(REMnux).Assembly, "remnux-logo.svg");
            var item = new GalleryItem
            {
                Name        = "REMnux",
                Publisher   = "REMnux Project",
                Description = $"REMnux is a Linux toolkit for reverse-engineering and analysing malware. Built on Ubuntu, it includes hundreds of tools for examining executables, documents, scripts and memory images (version {PinnedVersion}).",
                ThumbnailUri = logoUri,
                LogoUri      = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = OvaUrl,
                ArchiveRelativePath = "",
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = PinnedVersion,
                LastUpdated  = DateTime.UtcNow.ToString("o"),
                InitialUsername = "remnux",
                InitialPassword = "malware",
                Category     = "Security"
            };
            return new List<GalleryItem> { item };
        }
    }
}
