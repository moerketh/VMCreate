using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Scrapes the Security Onion GitHub Releases API to find the latest ISO download.
    /// </summary>
    public class SecurityOnion : IGalleryLoader
    {
        private const string ReleasesApiUrl = "https://api.github.com/repos/Security-Onion-Solutions/securityonion/releases/latest";

        private readonly IHttpClientFactory _clientFactory;

        public SecurityOnion(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(SecurityOnion).Assembly, "securityonion-logo.svg");
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github.v3+json");

            var response = await client.GetAsync(ReleasesApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "Unknown";

            // ISOs are not attached as GitHub release assets; construct the URL from the tag name.
            // Tag format: "2.4.210-20260302" → majorMinor = "2.4"
            // Download URL: https://download.securityonion.net/2.4/securityonion-2.4.210-20260302.iso
            string majorMinor = "2.4"; // safe default
            var tagParts = tagName.Split('-');
            if (tagParts.Length > 0)
            {
                var versionSegments = tagParts[0].Split('.');
                if (versionSegments.Length >= 2)
                    majorMinor = $"{versionSegments[0]}.{versionSegments[1]}";
            }

            var isoUrl = $"https://download.securityonion.net/{majorMinor}/securityonion-{tagName}.iso";

            var item = new GalleryItem
            {
                Name        = $"Security Onion {tagName}",
                Publisher   = "Security Onion Solutions",
                Description = "Security Onion is an open-source Linux distribution for threat hunting, enterprise security monitoring and log management. Bundles Elasticsearch, Logstash, Kibana, Zeek, Suricata and other IDS/SIEM tools.",
                ThumbnailUri = logoUri,
                LogoUri      = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = isoUrl,
                ArchiveRelativePath = "",
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = tagName,
                LastUpdated  = DateTime.UtcNow.ToString("o"),
                Category     = "Security"
            };

            return new List<GalleryItem> { item };
        }
    }
}
