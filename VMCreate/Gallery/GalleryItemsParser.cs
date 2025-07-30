using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using VMCreate;

namespace CreateVM
{
    public interface IGalleryItemsParser
    {
        List<GalleryItem> LoadJsonFromFile(string path);
        List<GalleryItem> LoadJsonFromFiles(string path);
        Task<List<GalleryItem>> LoadJsonFromUrl(string url);
        Task<List<GalleryItem>> LoadXmlFromUrl(string url);
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

        public async Task<List<GalleryItem>> LoadXmlFromUrl(string url)
        {
            var items = new List<GalleryItem>();
            try
            {
                _logger.LogDebug($"Downloading XML from {url}");
                using (HttpClient client = new HttpClient())
                {
                    string xml = await client.GetStringAsync(url);
                    var xdoc = XDocument.Parse(xml);
                    var images = new List<Dictionary<string, object>>();

                    var vhd = xdoc.Element("vhd");
                    if (vhd == null)
                    {
                        _logger.LogWarning("No vhd element found in XML");
                        return items;
                    }

                    var details = vhd.Element("details");
                    if (details == null)
                    {
                        _logger.LogWarning("No details element found in XML");
                        return items;
                    }

                    var descriptions = vhd.Element("descriptions")?.Elements("description").Select(d => d.Value).ToList() ?? new List<string>();
                    var image = vhd.Element("image");

                    if (image == null)
                    {
                        _logger.LogDebug("No image element found in XML");
                        return items;
                    }

                    var imageData = new Dictionary<string, object>
                    {
                        { "name", details.Element("name")?.Value ?? "" },
                        { "publisher", details.Element("publisher")?.Value ?? "" },
                        { "description", descriptions },
                        { "version", image.Element("version")?.Value ?? "" },
                        { "lastUpdated", details.Element("lastUpdated")?.Value ?? "" },
                        { "thumbnail", new Dictionary<string, string> { { "uri", image.Element("thumbnail")?.Element("uri")?.Value ?? "" } } },
                        { "logo", new Dictionary<string, string> { { "uri", image.Element("logo")?.Element("uri")?.Value ?? "" } } },
                        { "symbol", new Dictionary<string, string> { { "uri", image.Element("symbol")?.Element("uri")?.Value ?? "" } } },
                        { "disk", new Dictionary<string, string>
                            {
                                { "uri", image.Element("disk")?.Element("uri")?.Value ?? "" },
                                { "archiveRelativePath", image.Element("disk")?.Element("archiveRelativePath")?.Value ?? "" }
                            }
                        },
                        { "config", new Dictionary<string, string>
                            {
                                { "secureBoot", image.Element("secureBoot")?.Value ?? "" },
                                { "enhancedSessionTransportType", image.Element("enhancedSessionTransportType")?.Value ?? "" }
                            }
                    }
                    };

                    images.Add(imageData);

                    var json = JsonSerializer.Serialize(new { images }, new JsonSerializerOptions { WriteIndented = true });
                    items.AddRange(ParseJson(json));
                    _logger.LogDebug($"Parsed XML as JSON from {url}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to download or parse XML from {url}: {ex.Message}");
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
