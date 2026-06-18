using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Gallery loader for FLARE VM — a Mandiant reverse-engineering and malware-analysis
    /// environment built on top of a Windows 11 Enterprise Evaluation VHDX.
    ///
    /// The loader provides the Windows 11 dev environment VHDX from Microsoft's gallery,
    /// then post-boot customization steps handle:
    ///   1. Removing Windows Defender (via Defender Remover)
    ///   2. Disabling Windows Updates
    ///   3. Installing the FLARE VM toolkit (via install.ps1)
    ///
    /// The VM is configured for malware analysis with RDP access over Enhanced Session (HvSocket).
    /// </summary>
    public class FlareVm : IGalleryLoader
    {
        // Windows 11 Enterprise Evaluation VHDX — Microsoft's official dev environment.
        // This is the same image available through the Microsoft Hyper-V gallery,
        // ensuring a stable download URL that Microsoft maintains.
        private const string VhdxUrl = "https://download.microsoft.com/download/c/2/7/c275218e-b8d9-4adc-9344-ac3ee87349c3/WinDev2407Eval.HyperV.zip";

        // SHA-256 hash of the ZIP file for integrity verification.
        private const string VhdxChecksum = "B546C32894785DBAAC07543D913BDE9D6598972F76A440D6B5C00B2E73739684";

        // The VHDX is sysprep'd and will process our autounattend.xml during OOBE,
        // which creates a "flare" user and sets the Administrator password.
        // PowerShell Direct uses these credentials for post-boot customization.
        private const string DefaultUsername = "flare";
        private const string DefaultPassword = "flare";

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(FlareVm).Assembly, "flarevm-logo.png");

            var item = new GalleryItem
            {
                Name = "FLARE VM",
                Publisher = "Mandiant / Microsoft",
                Description = "FLARE VM is a Windows-based malware analysis environment by Mandiant. " +
                              "This gallery item downloads the Windows 11 Enterprise Evaluation VHDX " +
                              "and automates the setup of a hardened reverse-engineering workstation: " +
                              "Windows Defender is removed, updates are disabled, and the full FLARE VM toolkit " +
                              "is installed via Chocolatey/Boxstarter. RDP access is available via Enhanced Session.",
                ThumbnailUri = logoUri,
                SymbolUri = logoUri,
                DiskUri = VhdxUrl,
                Checksum = VhdxChecksum,
                ChecksumAlgorithm = "SHA256",
                SecureBoot = "true",
                SecureBootTemplate = "MicrosoftWindows",
                EnhancedSessionTransportType = "HvSocket",
                Version = "11-Enterprise-Eval-2407",
                LastUpdated = DateTime.UtcNow.ToString("o"),
                InitialUsername = DefaultUsername,
                InitialPassword = DefaultPassword,
                Category = "Security",
                IsRecommended = true,
                IsWindows = true,
                Tags = new() { "flare-vm", "windows", "malware-analysis" },
            };

            return new List<GalleryItem> { item };
        }
    }
}