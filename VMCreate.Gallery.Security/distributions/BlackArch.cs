using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Gallery loader for BlackArch Linux, delivered as an Arch Linux base
    /// with the BlackArch repositories added and a curated subset of tools
    /// installed post-boot by <c>BlackArchInstallStep</c> (tag: <c>blackarch</c>).
    /// <para>
    /// BlackArch upstream publishes only two stale disk images (a 2023.05 Slim
    /// ISO and a 2023.04 OVA) and supports the "Arch + BlackArch repos" model
    /// as a first-class install path. Riding the rolling Arch cloud image plus
    /// the live BlackArch repos yields a current pentesting install, sidesteps
    /// the outdated-ISO problem entirely, and reuses VMCreate's existing Arch
    /// qcow2 deployment pipeline (no ISO boot cycle).
    /// </para>
    /// <para>
    /// Static loader: <c>DiskUri</c> is the hardcoded Arch basic cloud image
    /// template, so no HTTP scrape is required to enumerate the item.
    /// </para>
    /// </summary>
    public class BlackArch : IGalleryLoader
    {
        private const string ArchTemplateUrl = "https://geo.mirror.pkgbuild.com/images/latest/Arch-Linux-x86_64-basic.qcow2";
        private const string SymbolUri = "https://archlinux.org/static/archlinux_common_style/favicon.png";

        public Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var galleryItem = new GalleryItem
            {
                Name = "BlackArch",
                Publisher = "BlackArch Project",
                Description = "BlackArch Linux as an Arch Linux base with the BlackArch repositories added post-boot, installing a curated subset of pentesting tools. Uses the current rolling Arch image, so this stays up to date where the BlackArch 2023 ISO is not.",
                ThumbnailUri = "",
                SymbolUri = SymbolUri,
                DiskUri = ArchTemplateUrl,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                LastUpdated = DateTime.UtcNow.ToString("o"),
                InitialUsername = "arch",
                InitialPassword = "arch",
                Category = "Security",
                Tags = new List<string> { "blackarch" }
            };
            return Task.FromResult(new List<GalleryItem> { galleryItem });
        }
    }
}
