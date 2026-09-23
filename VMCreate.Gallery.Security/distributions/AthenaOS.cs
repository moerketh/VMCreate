using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for Athena OS — an Arch/Nix-based distribution for
    /// offensive security and infosec learning, published as a single
    /// rolling live ISO. The same ISO installs both variants via the
    /// <c>Dory</c> terminal installer: <c>Athena Arch</c> (pacman-based)
    /// and <c>Athena Nix</c> (declarative). This loader surfaces one
    /// gallery item per variant so deployments can carry the intended
    /// target as metadata (<see cref="GalleryItem.LinuxDistro"/> + Tags)
    /// even though both download the same ISO.
    /// <para>
    /// The live environment ships no sshd and no Hyper-V KVP daemon, so
    /// installation is manual: Dory must be driven from the VM console
    /// (existing Linux-ISO UX). When the pin is bumped, update
    /// <see cref="PinnedVersion"/> and both URLs (the release tag and the
    /// lockstep-rolling <c>.sha256</c> checksum asset travel together).
    /// </para>
    /// </summary>
    public class AthenaOS : IGalleryLoader
    {
        private const string PinnedVersion = "26.03";
        private const string IsoUrl =
            "https://github.com/Athena-OS/athena/releases/download/v26.03/athenaos-live-x86_64.iso";
        private const string ChecksumUrl = IsoUrl + ".sha256";

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(AthenaOS).Assembly, "athena-logo.svg");

            var archVariant = new GalleryItem
            {
                Name = "Athena OS (Arch)",
                Publisher = "Athena OS Project",
                Description =
                    "Athena OS is a Linux distribution for Offensive Security and infosec learning, " +
                    "based on Arch Linux. This live ISO boots into the Dory terminal installer — " +
                    "choose the Athena Arch base there. Installation is manual; " +
                    $"pinned to release v{PinnedVersion}.",
                SymbolUri = logoUri,
                ThumbnailUri = logoUri,
                DiskUri = IsoUrl,
                ChecksumUri = ChecksumUrl,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = PinnedVersion,
                LastUpdated = DateTime.UtcNow.ToString("o"),
                Category = "Security",
                IsRecommended = false,
                LinuxDistro = LinuxDistro.Arch,
                Tags = new List<string> { "athena-arch" }
            };

            var nixVariant = new GalleryItem
            {
                Name = "Athena OS (Nix)",
                Publisher = "Athena OS Project",
                Description =
                    "Athena OS is a Linux distribution for Offensive Security and infosec learning. " +
                    "This live ISO boots into the Dory terminal installer — choose the Athena Nix " +
                    "(declarative, nix-based) variant there. Installation is manual; " +
                    "RDP is not supported on the Nix variant (post-boot installs through a package " +
                    "manager are structurally impossible on the nix store — use the console or SSH); " +
                    $"pinned to release v{PinnedVersion}.",
                SymbolUri = logoUri,
                ThumbnailUri = logoUri,
                DiskUri = IsoUrl,
                ChecksumUri = ChecksumUrl,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = PinnedVersion,
                LastUpdated = DateTime.UtcNow.ToString("o"),
                Category = "Security",
                IsRecommended = false,
                LinuxDistro = LinuxDistro.NixOs,
                Tags = new List<string> { "athena-nix" }
            };

            return new List<GalleryItem> { archVariant, nixVariant };
        }
    }
}