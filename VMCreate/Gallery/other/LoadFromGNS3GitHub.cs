using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class LoadFromGNS3GitHub : IGalleryLoader
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/GNS3/gns3-gui/releases/latest";
        private readonly ILogger<LoadFromGNS3GitHub> _logger;
        private readonly IHttpClientFactory _clientFactory;

        public LoadFromGNS3GitHub(ILogger<LoadFromGNS3GitHub> logger, IHttpClientFactory clientFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            List<GalleryItem> items = new List<GalleryItem>();
            try
            {
                var client = _clientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);
                string jsonResponse = await client.GetStringAsync(GitHubApiUrl, cancellationToken);
                JsonDocument doc = null;
                try
                {
                    doc = JsonDocument.Parse(jsonResponse);
                    JsonElement root = doc.RootElement;

                    string description = root.GetProperty("body").GetString() ?? "GNS3 Graphical Network Simulator VM Image";
                    string publishedAt = root.GetProperty("published_at").GetString() ?? DateTime.UtcNow.ToString("o");

                    JsonElement.ArrayEnumerator assets = root.GetProperty("assets").EnumerateArray();
                    JsonElement vmAsset = assets.FirstOrDefault(asset =>
                        asset.GetProperty("name").GetString().StartsWith("GNS3.VM.Hyper-V.", StringComparison.OrdinalIgnoreCase) &&
                        asset.GetProperty("name").GetString().EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

                    if (vmAsset.ValueKind == JsonValueKind.Undefined)
                    {
                        _logger.LogWarning("No GNS3 VM image for Hyper-V found in the latest release.");
                        return items;
                    }

                    string assetName = vmAsset.GetProperty("name").GetString();
                    string prefix = "GNS3.VM.Hyper-V.";
                    string suffix = ".zip";
                    string version = assetName.Substring(prefix.Length, assetName.Length - prefix.Length - suffix.Length);
                    string downloadUrl = vmAsset.GetProperty("browser_download_url").GetString();

                    GalleryItem galleryItem = new GalleryItem
                    {
                        Name = "GNS3 VM for Hyper-V " + version,
                        Publisher = "GNS3",
                        Description = description,
                        ThumbnailUri = "",
                        SymbolUri = "",
                        DiskUri = downloadUrl,
                        SecureBoot = "false",
                        EnhancedSessionTransportType = "HvSocket",
                        Version = version,
                        LastUpdated = publishedAt
                    };

                    items.Add(galleryItem);
                    _logger.LogDebug("Successfully loaded GNS3 VM image: " + galleryItem.Name);
                }
                finally
                {
                    doc?.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error loading GNS3 VM image from GitHub: " + ex.Message);
            }

            return items;
        }
    }
}
