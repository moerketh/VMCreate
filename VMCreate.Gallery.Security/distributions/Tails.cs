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
    /// The ISO is booted as a live DVD; VMCreate never writes a Tails disk —
    /// the OS is amnesic and gains nothing from installation, and the guest
    /// must stay unmodified for its privacy guarantees to hold.
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
            client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);

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

            // Find the .iso installation path (its target-file entry carries
            // an inline sha256 — the API is the publisher's own metadata).
            string? isoUrl = null;
            string? isoSha256 = null;
            foreach (var path in first.GetProperty("installation-paths").EnumerateArray())
            {
                if (path.GetProperty("type").GetString() == "iso")
                {
                    var targetFile = path.GetProperty("target-files")[0];
                    isoUrl = targetFile.GetProperty("url").GetString();
                    if (targetFile.TryGetProperty("sha256", out var sha))
                        isoSha256 = sha.GetString();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(isoUrl))
                throw new Exception($"Tails API: could not find .iso URL for version {version}.");

            // The API is publisher metadata fetched at runtime: a compromise of
            // it controls both URL and digest, so the digest can only prove
            // transport consistency (RuntimeChecksum), never a source pin.
            // Constrain the URL to the publisher's HTTPS hosts so the
            // metadata cannot redirect the download to an arbitrary origin.
            if (!Uri.TryCreate(isoUrl, UriKind.Absolute, out var isoUri)
                || isoUri.Scheme != Uri.UriSchemeHttps
                || (isoUri.Host != "tails.net" && isoUri.Host != "download.tails.net"))
            {
                throw new Exception($"Tails API: refusing non-tails.net or non-HTTPS image URL '{isoUrl}'.");
            }

            var item = new GalleryItem
            {
                Name = $"Tails {version}",
                Publisher = "Tails Project",
                Description = $"Tails is a portable OS that protects against surveillance and censorship. " +
                              $"It routes all internet connections through Tor and leaves no trace on the host machine (version {version}).",
                ThumbnailUri = logoUri,
                SymbolUri = logoUri,
                DiskUri = isoUrl,
                ChecksumAlgorithm = "SHA256",
                RuntimeChecksum = isoSha256,
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
