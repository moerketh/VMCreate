using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for the Rocky Linux 9 minimal amd64 ISO.
    /// Uses the stable "latest" redirect URL hosted on the official RESF download server.
    /// Update this loader if the major version of Rocky Linux changes.
    /// </summary>
    public class RockyLinux : IGalleryLoader
    {
        private const string IsoUrl = "https://download.rockylinux.org/pub/rocky/9/isos/x86_64/Rocky-9-latest-x86_64-minimal.iso";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(RockyLinux).Assembly, "rocky-linux-logo.svg");
            var item = new GalleryItem
            {
                Name        = "Rocky Linux 9 (latest)",
                Publisher   = "Rocky Enterprise Software Foundation",
                Description = "Rocky Linux is a community-built, RHEL-compatible enterprise distribution designed as a production-grade, binary-compatible replacement for CentOS. Minimal install ISO, always points to the latest Rocky 9 release.",
                ThumbnailUri = logoUri,
                LogoUri      = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = IsoUrl,
                ArchiveRelativePath = "",
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = "9-latest",
                LastUpdated  = DateTime.UtcNow.ToString("o")
            };
            return new List<GalleryItem> { item };
        }
    }
}
