using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Loads the latest stable Tails release from the official Tails JSON API.
    /// Tails is a privacy-focused live OS that routes all traffic through Tor.
    /// </summary>
    public class Tails : IGalleryLoader
    {
        private const string ApiUrl = "https://tails.net/install/v2/Tails/amd64/stable/latest.json";
        private readonly IHttpClientFactory _clientFactory;

        public Tails(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(Tails).Assembly, "tails-logo.svg");
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

            var response = await client.GetAsync(ApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(json);
            var installations = doc.RootElement.GetProperty("installations");

            if (installations.GetArrayLength() == 0)
                throw new Exception("Tails API returned no installations.");

            var first = installations[0];
            var version = first.GetProperty("version").GetString()
                ?? throw new Exception("Tails API: version field is missing.");

            // Find the .img installation path
            string imgUrl = null;
            foreach (var path in first.GetProperty("installation-paths").EnumerateArray())
            {
                if (path.GetProperty("type").GetString() == "img")
                {
                    imgUrl = path.GetProperty("target-files")[0]
                                 .GetProperty("url").GetString();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(imgUrl))
                throw new Exception($"Tails API: could not find .img URL for version {version}.");

            var item = new GalleryItem
            {
                Name = $"Tails {version}",
                Publisher = "Tails Project",
                Description = $"Tails is a portable OS that protects against surveillance and censorship. " +
                              $"It routes all internet connections through Tor and leaves no trace on the host machine (version {version}).",
                ThumbnailUri = logoUri,
                LogoUri = logoUri,
                SymbolUri = logoUri,
                DiskUri = imgUrl,
                ArchiveRelativePath = null,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = version,
                LastUpdated = DateTime.UtcNow.ToString("o"),
                Category = "Security"
            };

            return new List<GalleryItem> { item };
        }
    }
}
