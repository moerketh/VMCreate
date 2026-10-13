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
    /// Gallery loader for the Elastic SIEM appliance image: Rocky Linux 10
    /// Azure cloud image — the Hyper-V-native flavour (hv_storvsc/hv_vmbus
    /// in the initramfs, UEFI+BIOS dual layout with a populated ESP). The
    /// KVM-flavoured GenericCloud image boots to an emergency shell on
    /// Hyper-V Gen 2 ("Failed to mount /sysroot" — its initramfs cannot see
    /// the Hyper-V SCSI disk), so the Azure image is the correct base for a
    /// Hyper-V appliance despite the name.
    /// <para>
    /// First boot is customized by a cloud-init NoCloud seed ISO (ISO9660+
    /// Joliet — no UDF descriptor, see <c>CloudInitSeedBuilder</c>); the
    /// Elastic Stack customization step (<c>ElasticStackInstallStep</c>)
    /// then installs Elasticsearch, Kibana and a Fleet server from Elastic's
    /// rpm repository: the VM boots straight into a working SIEM.
    /// </para>
    /// <para>
    /// Downloads use the stable <c>latest</c> aliases; the concrete build
    /// (e.g. <c>10.2-20260525.0</c>) is scraped from the image directory
    /// listing for the card's version metadata. See
    /// working-docs/ELASTIC-SIEM.md for the full decision matrix.
    /// </para>
    /// </summary>
    public class ElasticSiem : IGalleryLoader
    {
        private const string ImagesIndexUrl = "https://dl.rockylinux.org/pub/rocky/10/images/x86_64/";
        private const string DiskUrl = ImagesIndexUrl + "Rocky-10-Azure-Base.latest.x86_64.vhdfixed.xz";
        private const string ChecksumUrl = DiskUrl + ".CHECKSUM";

        // Versioned file next to the latest alias: "Rocky-10-Azure-Base-10.2-20260525.0.x86_64.vhdfixed.xz"
        private static readonly Regex VersionedFileRegex = new(
            @"Rocky-10-Azure-Base-(\d+\.\d+-\d{8}\.\d+)\.x86_64\.vhdfixed\.xz", RegexOptions.Compiled);

        private readonly IHttpClientFactory _clientFactory;

        public ElasticSiem(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(ElasticSiem).Assembly, "elastic-siem-logo.svg");

            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);

            // Best-effort build metadata; the download URLs themselves are stable.
            string version = "unknown";
            DateTime lastUpdated = DateTime.UtcNow;
            try
            {
                var response = await client.GetAsync(ImagesIndexUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken);

                // The versioned twin of the latest alias carries the real build id.
                var versions = VersionedFileRegex.Matches(html)
                    .Select(m => m.Groups[1].Value)
                    .Distinct()
                    .OrderByDescending(s => s, StringComparer.Ordinal)
                    .ToList();
                if (versions.Count > 0)
                {
                    version = versions[0];
                    var datePart = version.Split('-').LastOrDefault() ?? "";
                    if (datePart.Length >= 8 && DateTime.TryParseExact(
                            datePart[..8], "yyyyMMdd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var parsed))
                    {
                        lastUpdated = parsed;
                    }
                }
            }
            catch (Exception)
            {
                // Metadata is decorative — the card still works with defaults.
            }

            var item = new GalleryItem
            {
                Name        = "Elastic SIEM",
                Publisher   = "Elastic + Rocky Linux",
                Description = "Headless single-node Elastic SIEM: Rocky Linux 10 (Hyper-V native cloud image) with Elasticsearch, Kibana and an Elastic Agent Fleet server installed on first deploy. SSH-only (no desktop); Kibana is reached over HTTP from the host. Optionally AI-enabled with a stored LLM endpoint.",
                ThumbnailUri = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = DiskUrl,
                ChecksumUri  = ChecksumUrl,
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = $"Rocky 10 (build {version})",
                LastUpdated  = lastUpdated.ToString("o"),
                Category     = "Security",
                LinuxDistro  = LinuxDistro.Rocky,
                // xz-compressed fixed VHD: extraction peels the .xz wrapper and
                // the inner .vhd is converted to VHDX. The override pins the
                // outer archive type pre-download regardless of alias drift.
                FileTypeOverride = "VHD",
                Tags         = new List<string> { "cloud-init", "elastic-siem", "headless" }
            };

            return new List<GalleryItem> { item };
        }
    }
}
