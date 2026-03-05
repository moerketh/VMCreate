using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class NixOS : IGalleryLoader
    {
        private const string BaseUrl = "https://channels.nixos.org/nixos-25.05/";
        private const string GraphicalIsoPath = "latest-nixos-graphical-x86_64-linux.iso";
        private const string MinimalIsoPath = "latest-nixos-minimal-x86_64-linux.iso";
        private const string ChannelVersion = "25.05";

        private const string? PublicLogoUrl = "https://nixos.org/logo/nixos-logo.png";

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(PublicLogoUrl, typeof(NixOS).Assembly, "nixos.svg");
            var galleryItems = new List<GalleryItem>();
            var lastModified = DateTime.UtcNow;

            // Graphical ISO
            var graphicalIsoUrl = $"{BaseUrl}{GraphicalIsoPath}";
            var graphicalFilename = $"latest-nixos-graphical-x86_64-linux-{ChannelVersion}.iso";

            galleryItems.Add(new GalleryItem
            {
                Name = "NixOS Graphical",
                Publisher = "NixOS Foundation",
                Description = $"NixOS with a graphical desktop environment (GNOME) for a reproducible and declarative system configuration (version {ChannelVersion})",
                ThumbnailUri = logoUri,
                LogoUri = logoUri,
                SymbolUri = logoUri,
                DiskUri = graphicalIsoUrl,
                ArchiveRelativePath = graphicalFilename,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = ChannelVersion,
                LastUpdated = lastModified.ToString("o")
            });

            // Minimal ISO
            var minimalIsoUrl = $"{BaseUrl}{MinimalIsoPath}";
            var minimalFilename = $"latest-nixos-minimal-x86_64-linux-{ChannelVersion}.iso";

            galleryItems.Add(new GalleryItem
            {
                Name = "NixOS Minimal",
                Publisher = "NixOS Foundation",
                Description = $"NixOS minimal installation for a lightweight, reproducible, and declarative system configuration (version {ChannelVersion})",
                ThumbnailUri = logoUri,
                LogoUri = logoUri,
                SymbolUri = logoUri,
                DiskUri = minimalIsoUrl,
                ArchiveRelativePath = minimalFilename,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = ChannelVersion,
                LastUpdated = lastModified.ToString("o")
            });

            return galleryItems;
        }
    }
}