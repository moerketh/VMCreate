using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Loads the latest Pentoo Full amd64 hardened daily build from the OSU mirror.
    /// Version metadata is fetched from versions.json; the download URL is the
    /// stable symlink kept current by the mirror.
    /// </summary>
    public class LoadPentooCurrent : IGalleryLoader
    {
        private const string MirrorBaseUrl  = "https://pentoo.osuosl.org/";
        private const string VersionsUrl    = MirrorBaseUrl + "latest-iso-symlinks/versions.json";
        private const string SymlinkUrl     = MirrorBaseUrl + "latest-iso-symlinks/pentoo-full-daily-amd64-hardened-latest.iso";
        private const string? PublicLogoUrl = "https://www.pentoo.org/icon.png";

        private readonly IHttpClientFactory _clientFactory;

        public LoadPentooCurrent(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(PublicLogoUrl, typeof(LoadPentooCurrent).Assembly, "pentoo-logo.svg");
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

            var response = await client.GetAsync(VersionsUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(json);

            string? version = null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("name",    out var nameProp)    &&
                    entry.TryGetProperty("type",    out var typeProp)    &&
                    entry.TryGetProperty("version", out var versionProp) &&
                    nameProp.GetString()  == "Pentoo Full amd64 hardened" &&
                    typeProp.GetString()  == "Daily" &&
                    versionProp.ValueKind != JsonValueKind.Null)
                {
                    version = versionProp.GetString();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(version))
                throw new Exception(
                    "Could not find 'Pentoo Full amd64 hardened' Daily entry in versions.json.");

            var galleryItem = new GalleryItem
            {
                Name        = "Pentoo Linux",
                Publisher   = "Pentoo Project",
                Description = "Pentoo is a Live CD and Live USB designed for penetration testing and " +
                              "security assessment. Based off Gentoo Linux.",
                LogoUri                     = logoUri,
                DiskUri                     = SymlinkUrl,
                SecureBoot                  = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version                     = version,
                LastUpdated                 = DateTime.UtcNow.ToString("o")
            };

            return new List<GalleryItem> { galleryItem };
        }
    }
}
