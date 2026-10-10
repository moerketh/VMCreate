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
    /// Gallery loader for the Elastic SIEM appliance image: Ubuntu Server 24.04
    /// LTS (Noble) cloud image — a headless, UEFI/GPT-bootable qcow2 — that the
    /// deployment pipeline converts to VHDX and customizes via cloud-init
    /// (NoCloud seed ISO) instead of an installer. Post-boot, the Elastic Stack
    /// customization step (<c>ElasticStackInstallStep</c>) stands up
    /// Elasticsearch, Kibana and a Fleet server: the VM boots straight into a
    /// working SIEM.
    /// <para>
    /// The stable <c>/current/</c> symlink is used for downloads; the build
    /// serial (e.g. <c>20260926</c>) is scraped from the release directory
    /// listing for the card's version/last-updated metadata.
    /// </para>
    /// </summary>
    public class ElasticSiem : IGalleryLoader
    {
        private const string ReleaseIndexUrl = "https://cloud-images.ubuntu.com/noble/";
        private const string BaseUrl = ReleaseIndexUrl + "current/";
        private const string DiskUrl = BaseUrl + "noble-server-cloudimg-amd64.img";
        private const string ChecksumUrl = BaseUrl + "SHA256SUMS";

        // Dated build directories: "20260926/" — the newest one is what /current/ points at.
        private static readonly Regex DatedDirRegex = new(@"(\d{8})/", RegexOptions.Compiled);

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
            string buildSerial = "unknown";
            DateTime lastUpdated = DateTime.UtcNow;
            try
            {
                var response = await client.GetAsync(ReleaseIndexUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken);

                var serials = DatedDirRegex.Matches(html)
                    .Select(m => m.Groups[1].Value)
                    .Where(s => s.Length == 8)
                    .OrderByDescending(s => s)
                    .ToList();
                if (serials.Count > 0)
                {
                    buildSerial = serials[0];
                    if (DateTime.TryParseExact(
                            buildSerial, "yyyyMMdd",
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
                Publisher   = "Elastic + Ubuntu Server",
                Description = "Headless single-node Elastic SIEM: Ubuntu Server 24.04 LTS cloud image with Elasticsearch, Kibana and an Elastic Agent Fleet server installed on first deploy. SSH-only (no desktop); Kibana is reached over HTTP from the host. Optionally AI-enabled with a stored LLM endpoint.",
                ThumbnailUri = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = DiskUrl,
                ChecksumUri  = ChecksumUrl,
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = $"24.04 LTS (build {buildSerial})",
                LastUpdated  = lastUpdated.ToString("o"),
                Category     = "Security",
                LinuxDistro  = LinuxDistro.Ubuntu,
                // The cloud image is qcow2 data despite the .img extension.
                FileTypeOverride = "QCOW2",
                Tags         = new List<string> { "cloud-init", "elastic-siem", "headless" }
            };

            return new List<GalleryItem> { item };
        }
    }
}
