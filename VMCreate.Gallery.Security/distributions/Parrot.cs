using Microsoft.Extensions.Logging;
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
    /// Single gallery loader for Parrot Security, Parrot Home, and Parrot HTB editions.
    /// Fetches the shared directory listing once and returns items for all editions.
    /// Automatically discovers the latest Parrot release version from the mirror index.
    /// </summary>
    public class Parrot : IGalleryLoader
    {
        private const string IndexUrl = "https://deb.parrot.sh/parrot/iso/";
        private const string SymbolUrl = "https://www.parrotsec.org/favicon.png";
        private readonly ILogger<Parrot> _logger;
        private readonly IHttpClientFactory _clientFactory;

        public Parrot(ILogger<Parrot> logger, IHttpClientFactory clientFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public Parrot(IHttpClientFactory clientFactory)
            : this(Microsoft.Extensions.Logging.Abstractions.NullLogger<Parrot>.Instance, clientFactory)
        {
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

            // ── Discover the latest Parrot release version ──
            var baseUrl = await DiscoverLatestVersionAsync(client, cancellationToken);
            if (baseUrl == null)
            {
                throw new InvalidOperationException($"Could not discover the latest Parrot version from {IndexUrl}.");
            }

            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(Parrot).Assembly, "parrot-logo.svg");

            var response = await client.GetAsync(baseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var htmlContent = await response.Content.ReadAsStringAsync();

            var items = new List<GalleryItem>();

            // ── Security Edition ──
            AddEdition(items, htmlContent, baseUrl, logoUri,
                isoPattern:   @"<a href=""(Parrot-security-[\d\.]+_amd64\.iso)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)",
                qcow2Pattern: @"<a href=""(Parrot-security-[\d\.]+_amd64\.qcow2(?:\.zip)?)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)",
                versionPattern: @"Parrot-security-([\d\.]+)_amd64\.",
                editionName: "Parrot Security OS",
                editionDesc: "includes a full set of penetration testing tools",
                isRecommended: true);

            // ── Home Edition ──
            AddEdition(items, htmlContent, baseUrl, logoUri,
                isoPattern:   @"<a href=""(Parrot-home-[\d\.]+_amd64\.iso)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)",
                qcow2Pattern: @"<a href=""(Parrot-home-[\d\.]+_amd64\.qcow2(?:\.zip)?)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)",
                versionPattern: @"Parrot-home-([\d\.]+)_amd64\.",
                editionName: "Parrot Home Edition",
                editionDesc: "for daily use with a focus on privacy and productivity",
                isRecommended: false);

            // ── HTB (Hack The Box) Edition ──
            AddEdition(items, htmlContent, baseUrl, logoUri,
                isoPattern:   @"<a href=""(Parrot-spin-htb-[\d\.]+_amd64\.iso)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)",
                qcow2Pattern: @"<a href=""(Parrot-spin-htb-[\d\.]+_amd64\.qcow2(?:\.zip)?)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)",
                versionPattern: @"Parrot-spin-htb-([\d\.]+)_amd64\.",
                editionName: "Parrot HTB Edition",
                editionDesc: "Hack The Box edition with pre-configured HTB tools and VPN integration",
                isRecommended: false);

            if (items.Count == 0)
            {
                throw new InvalidOperationException($"Could not find any Parrot editions in {baseUrl}.");
            }

            return items;
        }

        /// <summary>
        /// Fetches the Parrot ISO index page and returns the URL of the latest
        /// version directory (e.g. "https://deb.parrot.sh/parrot/iso/7.2/").
        /// Returns null if the version cannot be determined.
        /// </summary>
        private async Task<string> DiscoverLatestVersionAsync(HttpClient client, CancellationToken cancellationToken)
        {
            try
            {
                var response = await client.GetAsync(IndexUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken);

                // Match directory links like "7.2/", "7.1/", "6.4/" — must end with "/"
                var versionMatches = Regex.Matches(html, @"<a\s+href=""(\d+(?:\.\d+)+)/""");
                if (versionMatches.Count == 0)
                {
                    _logger.LogWarning("No Parrot version directories found in index at {IndexUrl}.", IndexUrl);
                    return null;
                }

                // Parse versions and pick the highest (semantic version sort)
                var versions = versionMatches
                    .Cast<Match>()
                    .Select(m => m.Groups[1].Value)
                    .Select(v => Version.TryParse(v, out var parsed) ? (Raw: v, Parsed: parsed) : (Raw: v, Parsed: (Version)null))
                    .Where(t => t.Parsed != null)
                    .OrderByDescending(t => t.Parsed)
                    .ToList();

                if (versions.Count == 0)
                {
                    _logger.LogWarning("Could not parse any Parrot version numbers from index at {IndexUrl}.", IndexUrl);
                    return null;
                }

                var latest = versions[0].Raw;
                _logger.LogDebug("Discovered latest Parrot version: {Version}", latest);
                return $"{IndexUrl}{latest}/";
            }
            catch (OperationCanceledException)
            {
                throw; // propagate cancellation
            }
            catch (HttpRequestException)
            {
                throw; // propagate HTTP errors (e.g. server 500)
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to discover latest Parrot version from {IndexUrl}.", IndexUrl);
                return null;
            }
        }

        private void AddEdition(List<GalleryItem> items, string html, string baseUrl, string logoUri,
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
                    DiskUri = baseUrl + filename,
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
                    DiskUri = baseUrl + filename,
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
