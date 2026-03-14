using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Single gallery loader for both Parrot Security and Parrot Home editions.
    /// Fetches the shared directory listing once and returns items for both editions.
    /// </summary>
    public class Parrot : IGalleryLoader
    {
        private const string BaseUrl = "https://deb.parrot.sh/parrot/iso/7.1/";
        private const string SymbolUrl = "https://www.parrotsec.org/favicon.png";
        private readonly IHttpClientFactory _clientFactory;

        public Parrot(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(Parrot).Assembly, "parrot-logo.svg");
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

            var response = await client.GetAsync(BaseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var htmlContent = await response.Content.ReadAsStringAsync();

            var items = new List<GalleryItem>();

            // ── Security Edition ──
            AddEdition(items, htmlContent, logoUri,
                isoPattern:   @"<a href=""(Parrot-security-[\d\.]+_amd64\.iso)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)",
                qcow2Pattern: @"<a href=""(Parrot-security-[\d\.]+_amd64\.qcow2)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)",
                versionPattern: @"Parrot-security-([\d\.]+)_amd64\.",
                editionName: "Parrot Security OS",
                editionDesc: "includes a full set of penetration testing tools",
                isRecommended: true);

            // ── Home Edition ──
            AddEdition(items, htmlContent, logoUri,
                isoPattern:   @"<a href=""(Parrot-home-[\d\.]+_amd64\.iso)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)",
                qcow2Pattern: @"<a href=""(Parrot-home-[\d\.]+_amd64\.qcow2)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)",
                versionPattern: @"Parrot-home-([\d\.]+)_amd64\.",
                editionName: "Parrot Home Edition",
                editionDesc: "for daily use with a focus on privacy and productivity",
                isRecommended: false);

            if (items.Count == 0)
                throw new Exception("Could not find any Parrot editions.");

            return items;
        }

        private void AddEdition(List<GalleryItem> items, string html, string logoUri,
            string isoPattern, string qcow2Pattern, string versionPattern,
            string editionName, string editionDesc, bool isRecommended)
        {
            var isoMatch = Regex.Match(html, isoPattern, RegexOptions.Singleline);
            var qcow2Match = Regex.Match(html, qcow2Pattern, RegexOptions.Singleline);

            if (isoMatch.Success)
            {
                var filename = isoMatch.Groups[1].Value;
                var version = ExtractVersion(filename, versionPattern);
                var lastUpdated = ParseDate(isoMatch.Groups[2].Value);

                items.Add(new GalleryItem
                {
                    Name = editionName,
                    Publisher = "Parrot Project",
                    Description = $"{editionName} ISO installer, {editionDesc} (version {version})",
                    ThumbnailUri = logoUri,
                    SymbolUri = SymbolUrl,
                    DiskUri = BaseUrl + filename,
                    SecureBoot = "false",
                    EnhancedSessionTransportType = "HvSocket",
                    Version = version,
                    LastUpdated = lastUpdated.ToString("o"),
                    InitialUsername = "user",
                    InitialPassword = "parrot",
                    Category = "Security",
                    IsRecommended = isRecommended
                });
            }

            if (qcow2Match.Success)
            {
                var filename = qcow2Match.Groups[1].Value;
                var version = ExtractVersion(filename, versionPattern);
                var lastUpdated = ParseDate(qcow2Match.Groups[2].Value);

                items.Add(new GalleryItem
                {
                    Name = $"{editionName}",
                    Publisher = "Parrot Project",
                    Description = $"{editionName} pre-installed disk image, {editionDesc} (version {version})",
                    ThumbnailUri = logoUri,
                    SymbolUri = SymbolUrl,
                    DiskUri = BaseUrl + filename,
                    SecureBoot = "false",
                    EnhancedSessionTransportType = "HvSocket",
                    Version = version,
                    LastUpdated = lastUpdated.ToString("o"),
                    InitialUsername = "user",
                    InitialPassword = "parrot",
                    Category = "Security",
                    IsRecommended = isRecommended
                });
            }
        }

        private static string ExtractVersion(string filename, string pattern)
        {
            var versionMatch = Regex.Match(filename, pattern);
            if (!versionMatch.Success)
                throw new Exception($"Could not extract version from filename: {filename}");
            return versionMatch.Groups[1].Value;
        }

        private static DateTime ParseDate(string dateStr)
        {
            return DateTime.ParseExact(dateStr, "dd-MMM-yyyy HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal);
        }
    }
}
