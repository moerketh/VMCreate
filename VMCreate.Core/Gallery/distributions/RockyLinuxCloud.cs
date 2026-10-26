using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Gallery loader for the Rocky Linux cloud-image card: one item with a
    /// release variant per supported major (Rocky 9 default, Rocky 10),
    /// selected on the VM settings page or via the CLI <c>--release</c> flag.
    /// <para>
    /// Both variants use the stable <c>latest</c> aliases of the Azure-Base
    /// cloud image — the Hyper-V-native flavour (hv_storvsc/hv_vmbus in the
    /// initramfs, UEFI+BIOS dual layout with a populated ESP). The
    /// KVM-flavoured GenericCloud image boots to an emergency shell on
    /// Hyper-V Gen 2 ("Failed to mount /sysroot" — its initramfs cannot see
    /// the Hyper-V SCSI disk), so the Azure image is the correct base
    /// despite the name. The concrete build (e.g. <c>9.8-20260525.0</c>) is
    /// scraped from each major's image directory listing for the card's
    /// version metadata; the download URLs themselves are stable.
    /// </para>
    /// <para>
    /// First boot is customized by a cloud-init NoCloud seed ISO (ISO9660+
    /// Joliet — no UDF descriptor, see <c>CloudInitSeedBuilder</c>), which
    /// provisions the <c>vmcreate</c> SSH automation user and grows the root
    /// filesystem. The image ships no desktop, so it deploys headless:
    /// SSH-only management access, no RDP backend.
    /// </para>
    /// </summary>
    public class RockyLinuxCloud : IGalleryLoader
    {
        // dl.rockylinux.org is a round-robin alias over download.rockylinux.org
        // and hosts the images tree (download.rockylinux.org carries the isos).
        private const string ImagesBase = "https://dl.rockylinux.org/pub/rocky/";

        private const string Rocky9IndexUrl  = ImagesBase + "9/images/x86_64/";
        private const string Rocky9DiskUrl   = Rocky9IndexUrl + "Rocky-9-Azure-Base.latest.x86_64.vhdfixed.xz";
        private const string Rocky9Checksum  = Rocky9DiskUrl + ".CHECKSUM";

        private const string Rocky10IndexUrl = ImagesBase + "10/images/x86_64/";
        private const string Rocky10DiskUrl  = Rocky10IndexUrl + "Rocky-10-Azure-Base.latest.x86_64.vhdfixed.xz";
        private const string Rocky10Checksum  = Rocky10DiskUrl + ".CHECKSUM";

        // Versioned files next to the latest aliases:
        // "Rocky-9-Azure-Base-9.8-20260525.0.x86_64.vhdfixed.xz"
        // The LVM (disk-layout) and GenericCloud (KVM-flavour) twins must not
        // leak into the Azure-Base version scrape.
        private static readonly Regex VersionedFileRegex = new(
            @"Rocky-9-Azure-Base-(\d+\.\d+-\d{8}\.\d+)\.x86_64\.vhdfixed\.xz", RegexOptions.Compiled);

        private static readonly Regex Rocky10VersionedFileRegex = new(
            @"Rocky-10-Azure-Base-(\d+\.\d+-\d{8}\.\d+)\.x86_64\.vhdfixed\.xz", RegexOptions.Compiled);

        private readonly IHttpClientFactory _clientFactory;

        public RockyLinuxCloud(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(RockyLinuxCloud).Assembly, "rocky-linux-logo.svg");

            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);

            // Best-effort build metadata per major; the download URLs are
            // static latest-aliases, so the card works with defaults alone.
            var rocky9  = await TryScrapeBuildAsync(client, Rocky9IndexUrl,  VersionedFileRegex, cancellationToken);
            var rocky10 = await TryScrapeBuildAsync(client, Rocky10IndexUrl, Rocky10VersionedFileRegex, cancellationToken);

            var variants = new List<ReleaseVariant>
            {
                new()
                {
                    Id = "rocky-9",
                    DisplayName = $"Rocky 9 ({rocky9.Version})",
                    Description = "Enterprise RHEL 9-compatible baseline.",
                    DiskUri = Rocky9DiskUrl,
                    ChecksumUri = Rocky9Checksum,
                    Version = rocky9.Version,
                    LastUpdated = rocky9.LastUpdated,
                    IsDefault = true
                },
                new()
                {
                    Id = "rocky-10",
                    DisplayName = $"Rocky 10 ({rocky10.Version})",
                    Description = "Current RHEL 10-compatible major.",
                    DiskUri = Rocky10DiskUrl,
                    ChecksumUri = Rocky10Checksum,
                    Version = rocky10.Version,
                    LastUpdated = rocky10.LastUpdated,
                    IsDefault = false
                }
            };

            var item = new GalleryItem
            {
                Name        = "Rocky Linux (Cloud Image)",
                Publisher   = "Rocky Enterprise Software Foundation",
                Description = "Rocky Linux as a ready-to-boot Hyper-V cloud image — no manual installation. Community-built and binary-compatible with RHEL. The release picker chooses the major (9 = RHEL 9 baseline, 10 = current). Deploys headless with SSH access; the deployment's public key provisions the login user.",
                ThumbnailUri = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = Rocky9DiskUrl,
                ChecksumUri  = Rocky9Checksum,
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = rocky9.Version,
                LastUpdated  = rocky9.LastUpdated,
                LinuxDistro  = LinuxDistro.Rocky,
                ReleaseVariants = variants,
                // xz-compressed fixed VHD: extraction peels the .xz wrapper and
                // the inner .vhd is converted to VHDX. The override pins the
                // outer archive type pre-download regardless of alias drift —
                // "vhdfixed" is not a recognized extension for inference.
                FileTypeOverride = "VHD",
                Tags         = new List<string> { "cloud-init", "headless" }
            };

            return new List<GalleryItem> { item };
        }

        /// <summary>Build id + date scraped from one major's image directory.</summary>
        private sealed record ScrapedBuild(string Version, string? LastUpdated);

        private static async Task<ScrapedBuild> TryScrapeBuildAsync(
            HttpClient client, string indexUrl, Regex versionedFileRegex, CancellationToken cancellationToken)
        {
            try
            {
                var response = await client.GetAsync(indexUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken);

                // The versioned twin of the latest alias carries the real build id.
                var versions = versionedFileRegex.Matches(html)
                    .Select(m => m.Groups[1].Value)
                    .Distinct()
                    .OrderByDescending(s => s, StringComparer.Ordinal)
                    .ToList();
                if (versions.Count == 0)
                    return new ScrapedBuild("unknown", null);

                string version = versions[0];
                var datePart = version.Split('-').LastOrDefault() ?? "";
                if (datePart.Length >= 8 && DateTime.TryParseExact(
                        datePart[..8], "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    return new ScrapedBuild(version, parsed.ToString("o"));
                }
                return new ScrapedBuild(version, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Metadata is decorative — the card still works with defaults.
                return new ScrapedBuild("unknown", null);
            }
        }
    }
}