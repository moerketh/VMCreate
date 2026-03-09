using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Scrapes the Debian cdimage directory listing to find the latest
    /// amd64 network-install ISO for the current stable release.
    /// </summary>
    public class Debian : IGalleryLoader
    {
        private const string BaseUrl = "https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/";

        private readonly IHttpClientFactory _clientFactory;

        public Debian(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(Debian).Assembly, "debian-logo.svg");
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

            var response = await client.GetAsync(BaseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            // Match filename in an href attribute — works with Apache autoindex table format
            var pattern = @"href=""(debian-[\d\.]+-amd64-netinst\.iso)""";
            var match = Regex.Match(html, pattern);

            if (!match.Success)
                throw new Exception("Could not find Debian amd64 netinstall ISO in the directory listing.");

            var filename = match.Groups[1].Value;

            var versionMatch = Regex.Match(filename, @"debian-([\d\.]+)-amd64-netinst\.iso");
            var version = versionMatch.Success ? versionMatch.Groups[1].Value : "Unknown";

            var lastUpdated = DateTime.UtcNow;

            var item = new GalleryItem
            {
                Name        = $"Debian {version}",
                Publisher   = "The Debian Project",
                Description = $"Debian is a universal, community-driven Linux distribution renowned for its stability, security, and immense software repository. This is the amd64 network-install ISO for the current stable release (version {version}).",
                ThumbnailUri = logoUri,
                LogoUri      = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = BaseUrl + filename,
                ArchiveRelativePath = "",
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = version,
                LastUpdated  = lastUpdated.ToString("o")
            };

            return new List<GalleryItem> { item };
        }
    }
}
