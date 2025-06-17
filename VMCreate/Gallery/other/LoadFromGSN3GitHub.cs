using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class LoadFromGNS3GitHub : IGalleryLoader
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/GNS3/gns3-gui/releases/latest";
        private static readonly HttpClient _httpClient = new HttpClient();

        public LoadFromGNS3GitHub()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "VMCreate-GalleryLoader/1.0");
        }

        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            List<GalleryItem> items = new List<GalleryItem>();
            try
            {
                string jsonResponse = await _httpClient.GetStringAsync(GitHubApiUrl);
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
                        Console.WriteLine("Warning: No GNS3 VM image for Hyper-V found in the latest release.");
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
                        LogoUri = "https://github.com/GNS3/gns3-gui/raw/master/docs/gns3_logo.png",
                        SymbolUri = "",
                        DiskUri = downloadUrl,
                        ArchiveRelativePath = "GNS3 VM - disk001.vhd",
                        SecureBoot = "false",
                        EnhancedSessionTransportType = "HvSocket",
                        Version = version,
                        LastUpdated = publishedAt
                    };

                    items.Add(galleryItem);
                    Console.WriteLine("Successfully loaded GNS3 VM image: " + galleryItem.Name);
                }
                finally
                {
                    if (doc != null)
                    {
                        doc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error loading GNS3 VM image from GitHub: " + ex.Message);
            }

            return items;
        }
    }
}