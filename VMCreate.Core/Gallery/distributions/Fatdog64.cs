using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Static loader for Fatdog64 Linux — an independent, Puppy-style distribution
    /// that boots entirely into RAM (no disk required). The VM is stateless: it runs
    /// from guest memory, and powering off discards every change made during the session.
    /// Update <see cref="PinnedVersion"/> and <see cref="IsoUrl"/> on each new stable release.
    /// See https://distro.ibiblio.org/fatdog/iso/ for the latest version.
    /// </summary>
    public class Fatdog64 : IGalleryLoader
    {
        private const string PinnedVersion = "904";
        private const string IsoUrl = "https://distro.ibiblio.org/fatdog/iso/Fatdog64-904.iso";
        private const string ChecksumUrl = IsoUrl + ".md5sum.txt";

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(Fatdog64).Assembly, "fatdog64-logo.svg");
            var item = new GalleryItem
            {
                Name        = $"Fatdog64 {PinnedVersion}",
                Publisher   = "Fatdog64 Team",
                Description = $"Fatdog64 is an independent, lightweight 64-bit distribution in the Puppy Linux family. The entire system loads into RAM and runs from memory: no virtual hard disk is created, nothing is written to disk, and powering off wipes the session — ideal as a disposable clean-room browser, forensics or malware-triage machine. Boots straight to a root desktop with no login prompt. Decline the save-file prompt at shutdown to stay stateless (version {PinnedVersion}).",
                ThumbnailUri = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = IsoUrl,
                ChecksumUri  = ChecksumUrl,
                ChecksumAlgorithm = "md5",
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                IsDiskless   = true,
                Version      = PinnedVersion,
                LastUpdated  = DateTime.UtcNow.ToString("o"),
                InitialUsername = "root",
                InitialPassword = ""
            };
            return new List<GalleryItem> { item };
        }
    }
}
