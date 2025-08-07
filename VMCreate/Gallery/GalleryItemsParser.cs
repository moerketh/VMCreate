using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VMCreate;

namespace CreateVM
{
    public interface IGalleryItemsParser
    {
        List<GalleryItem> LoadJsonFromFile(string path);
        List<GalleryItem> LoadJsonFromFiles(string path);
        Task<List<GalleryItem>> LoadJsonFromUrl(string url);
    }

    public class GalleryItemsParser : IGalleryItemsParser
    {
        private readonly ILogger<GalleryItemsParser> _logger;

        public GalleryItemsParser(ILogger<GalleryItemsParser> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<GalleryItem>> LoadJsonFromUrl(string url)
        {
            try
            {
                _logger.LogDebug($"Downloading JSON from {url}");
                using (HttpClient client = new HttpClient())
                {
                    string json = await client.GetStringAsync(url);
                    var items = ParseJson(json);
                    _logger.LogDebug($"Parsed JSON from {url}");
                    return items;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to download or parse JSON from {url}: {ex.Message}");
                return new List<GalleryItem>();
            }
        }

        public List<GalleryItem> LoadJsonFromFiles(string path)
        {
            var items = new List<GalleryItem>();
            try
            {
                foreach (string file in Directory.GetFiles(path, "*.json"))
                {
                    items.AddRange(LoadJsonFromFile(file));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to parse JSON from {path}: {ex.Message}");
            }
            return items;
        }

        public List<GalleryItem> LoadJsonFromFile(string path)
        {
            var items = new List<GalleryItem>();
            try
            {
                _logger.LogDebug($"Parsing local JSON file: {path}");
                string json = File.ReadAllText(path);
                items.AddRange(ParseJson(json));
                _logger.LogDebug($"Parsed JSON from {path}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to parse JSON from {path}: {ex.Message}");
            }
            return items;
        }

        private List<GalleryItem> ParseJson(string json)
        {
            var items = new List<GalleryItem>();
            try
            {
                var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
                foreach (var image in doc.RootElement.GetProperty("images").EnumerateArray())
                {
                    try
                    {
                        // Critical keys (required)
                        if (!image.TryGetProperty("name", out var nameProp) || !image.TryGetProperty("disk", out var diskProp) || !diskProp.TryGetProperty("uri", out var diskUriProp))
                        {
                            _logger.LogError("Skipping image: Missing critical keys (name or disk.uri)");
                            continue;
                        }

                        string name = nameProp.GetString();
                        string diskUri = diskUriProp.GetString();
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(diskUri))
                        {
                            _logger.LogDebug("Skipping image: Empty name or diskUri");
                            continue;
                        }

                        var item = new GalleryItem
                        {
                            Name = name,
                            Publisher = image.TryGetProperty("publisher", out var publisherProp) ? publisherProp.GetString() ?? "" : "",
                            Description = image.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.Array
                                ? string.Join(" ", descProp.EnumerateArray().Select(e => e.GetString())).Trim()
                                : (descProp.ValueKind == JsonValueKind.String ? descProp.GetString() : ""),
                            ThumbnailUri = image.TryGetProperty("thumbnail", out var thumbProp) && thumbProp.TryGetProperty("uri", out var thumbUriProp)
                                ? thumbUriProp.GetString() ?? ""
                                : "",
                            LogoUri = image.TryGetProperty("logo", out var logoProp) && logoProp.TryGetProperty("uri", out var logoUriProp)
                                ? logoUriProp.GetString() ?? ""
                                : "",
                            SymbolUri = image.TryGetProperty("symbol", out var symbolProp) && symbolProp.TryGetProperty("uri", out var symbolUriProp)
                                ? symbolUriProp.GetString() ?? ""
                                : "",
                            DiskUri = diskUri,
                            ArchiveRelativePath = diskProp.TryGetProperty("archiveRelativePath", out var archivePathProp)
                                ? archivePathProp.GetString() ?? ""
                                : "",
                            SecureBoot = image.TryGetProperty("config", out var configProp) && configProp.TryGetProperty("secureBoot", out var secureBootProp)
                                ? secureBootProp.GetString() ?? ""
                                : "",
                            EnhancedSessionTransportType = configProp.TryGetProperty("enhancedSessionTransportType", out var enhancedProp)
                                ? enhancedProp.GetString() ?? ""
                                : "",
                            Version = image.TryGetProperty("version", out var versionProp) ? versionProp.GetString() ?? "" : "",
                            LastUpdated = image.TryGetProperty("lastUpdated", out var lastUpdatedProp) ? lastUpdatedProp.GetString() ?? "" : ""
                        };

                        items.Add(item);
                    }
                    catch (KeyNotFoundException ex)
                    {
                        _logger.LogError($"Error parsing image: Missing key '{ex.Message}'");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error parsing image: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error parsing JSON: {ex.Message}");
            }
            return items;
        }

    }
}
