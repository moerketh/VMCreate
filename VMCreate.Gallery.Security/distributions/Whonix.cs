using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for the Whonix LXQt combined VirtualBox appliance.
    /// Whonix now ships a single unified OVA containing both the Gateway and Workstation VMs.
    /// Update <see cref="PinnedVersion"/> when a new stable release is available
    /// at https://www.whonix.org/wiki/VirtualBox.
    /// </summary>
    public class Whonix : IGalleryLoader
    {
        private const string PinnedVersion = "18.1.4.2";
        private const string OvaUrl        = "https://www.whonix.org/download/ova/18.1.4.2/Whonix-LXQt-18.1.4.2.Intel_AMD64.ova";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(Whonix).Assembly, "whonix-logo.svg");
            var item = new GalleryItem
            {
                Name        = $"Whonix {PinnedVersion} LXQt",
                Publisher   = "Whonix Project",
                Description = $"Whonix is a privacy-focused OS that routes all traffic through the Tor anonymity network. This LXQt edition ships as a unified VirtualBox appliance containing both the Gateway and Workstation VMs (version {PinnedVersion}).",
                ThumbnailUri = logoUri,
                LogoUri      = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = OvaUrl,
                ArchiveRelativePath = "",
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = PinnedVersion,
                LastUpdated  = DateTime.UtcNow.ToString("o"),
                InitialUsername = "user",
                InitialPassword = "changeme",
                Category     = "Security"
            };

            return new List<GalleryItem> { item };
        }
    }
}
