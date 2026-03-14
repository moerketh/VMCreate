using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public interface IGalleryItemsParser
    {
        List<GalleryItem> LoadJsonFromFile(string path);
        List<GalleryItem> LoadJsonFromFiles(string path);
        Task<List<GalleryItem>> LoadJsonFromUrl(string url, CancellationToken cancellationToken = default);
    }

    public class GalleryItemsParser : IGalleryItemsParser
    {
        private readonly ILogger<GalleryItemsParser> _logger;
        private readonly IHttpClientFactory _clientFactory;

        public GalleryItemsParser(ILogger<GalleryItemsParser> logger, IHttpClientFactory clientFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadJsonFromUrl(string url, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Downloading JSON from {Url}", url);
                var client = _clientFactory.CreateClient();
                string json = await client.GetStringAsync(url, cancellationToken);
                var items = ParseJson(json);
                _logger.LogDebug("Parsed JSON from {Url}", url);
                return items;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to download or parse JSON from {Url}: {ErrorMessage}", url, ex.Message);
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
                _logger.LogWarning("Failed to parse JSON from {Path}: {ErrorMessage}", path, ex.Message);
            }
            return items;
        }

        public List<GalleryItem> LoadJsonFromFile(string path)
        {
            var items = new List<GalleryItem>();
            try
            {
                _logger.LogDebug("Parsing local JSON file: {Path}", path);
                string json = File.ReadAllText(path);
                items.AddRange(ParseJson(json));
                _logger.LogDebug("Parsed JSON from {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse JSON from {Path}: {ErrorMessage}", path, ex.Message);
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

                        // Extract "config" properties up-front to avoid calling
                        // TryGetProperty on a default (Undefined) JsonElement,
                        // which would throw InvalidOperationException.
                        string secureBoot = "";
                        string enhancedSessionTransportType = "";
                        if (image.TryGetProperty("config", out var configProp))
                        {
                            if (configProp.TryGetProperty("secureBoot", out var secureBootProp))
                                secureBoot = secureBootProp.GetString() ?? "";
                            if (configProp.TryGetProperty("enhancedSessionTransportType", out var enhancedProp))
                                enhancedSessionTransportType = enhancedProp.GetString() ?? "";
                        }

                        // Extract "description" — may be a string or an array of strings.
                        string description = "";
                        if (image.TryGetProperty("description", out var descProp))
                        {
                            description = descProp.ValueKind == JsonValueKind.Array
                                ? string.Join(" ", descProp.EnumerateArray().Select(e => e.GetString())).Trim()
                                : descProp.ValueKind == JsonValueKind.String
                                    ? descProp.GetString() ?? ""
                                    : "";
                        }

                        var item = new GalleryItem
                        {
                            Name = name,
                            Publisher = image.TryGetProperty("publisher", out var publisherProp) ? publisherProp.GetString() ?? "" : "",
                            Description = description,
                            ThumbnailUri = image.TryGetProperty("thumbnail", out var thumbProp) && thumbProp.TryGetProperty("uri", out var thumbUriProp)
                                ? thumbUriProp.GetString() ?? ""
                                : "",
                            SymbolUri = image.TryGetProperty("symbol", out var symbolProp) && symbolProp.TryGetProperty("uri", out var symbolUriProp)
                                ? symbolUriProp.GetString() ?? ""
                                : "",
                            DiskUri = diskUri,
#pragma warning disable CS0618 // ArchiveRelativePath is obsolete — still read from JSON for backward compat
                            ArchiveRelativePath = diskProp.TryGetProperty("archiveRelativePath", out var archivePathProp)
                                ? archivePathProp.GetString() ?? ""
                                : "",
#pragma warning restore CS0618
                            SecureBoot = secureBoot,
                            EnhancedSessionTransportType = enhancedSessionTransportType,
                            Version = image.TryGetProperty("version", out var versionProp) ? versionProp.GetString() ?? "" : "",
                            LastUpdated = image.TryGetProperty("lastUpdated", out var lastUpdatedProp) ? lastUpdatedProp.GetString() ?? "" : ""
                        };

                        items.Add(item);
                    }
                    catch (KeyNotFoundException ex)
                    {
                        _logger.LogError("Error parsing image: Missing key '{MissingKey}'", ex.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error parsing image: {ErrorMessage}", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing JSON: {ErrorMessage}", ex.Message);
            }
            return items;
        }
    }
}

