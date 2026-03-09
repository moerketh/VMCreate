using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for Alpine Linux (virtual/virt flavour) — a minimal ISO
    /// stripped of physical-hardware drivers, optimised for virtual machines.
    /// Update <see cref="PinnedVersion"/> and <see cref="IsoUrl"/> on each new stable release.
    /// See https://alpinelinux.org/downloads/ for the latest version.
    /// </summary>
    public class AlpineLinux : IGalleryLoader
    {
        private const string PinnedVersion = "3.21.3";
        private const string IsoUrl = "https://dl-cdn.alpinelinux.org/alpine/v3.21/releases/x86_64/alpine-virt-3.21.3-x86_64.iso";
        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(AlpineLinux).Assembly, "alpine-logo.svg");
            var item = new GalleryItem
            {
                Name        = $"Alpine Linux {PinnedVersion}",
                Publisher   = "Alpine Linux Development Team",
                Description = $"Alpine Linux is a security-oriented, lightweight distribution built around musl libc and BusyBox. The virt flavour is stripped to the minimum for use in virtual machines, making it ideal for containers and low-resource setups (version {PinnedVersion}).",
                ThumbnailUri = logoUri,
                LogoUri      = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = IsoUrl,
                ArchiveRelativePath = "",
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = PinnedVersion,
                LastUpdated  = DateTime.UtcNow.ToString("o"),
                InitialUsername = "root",
                InitialPassword = ""
            };
            return new List<GalleryItem> { item };
        }
    }
}
