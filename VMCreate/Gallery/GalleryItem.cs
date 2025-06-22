using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace VMCreateVM
{
    public class GalleryItem
    {
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Description { get; set; }
        public string ThumbnailUri { get; set; }
        public string LogoUri { get; set; }
        public string DiskUri { get; set; }
        public string SymbolUri { get; set; }
        public string ArchiveRelativePath { get; set; }
        public string SecureBoot { get; set; }
        public string EnhancedSessionTransportType { get; set; }
        public string Version { get; set; }
        public string LastUpdated { get; set; }

        public string FileType
        {
            get
            {
                string path = !string.IsNullOrEmpty(ArchiveRelativePath) ? ArchiveRelativePath : DiskUri;
                if (string.IsNullOrEmpty(path))
                    return "Unknown";

                string extension = Path.GetExtension(path).ToLower().TrimStart('.');
                string baseName = Path.GetFileNameWithoutExtension(path);

                // Handle common compression extensions
                switch (extension.ToLower())
                {
                    case "7z":
                    case "zip":
                    case "gz":
                        extension = ArchiveRelativePath != null
                        ?  Path.GetExtension(ArchiveRelativePath).ToLower().TrimStart('.')
                        :  Path.GetExtension(baseName).ToLower().TrimStart('.');
                        break;
                    case "xz":                    
                        extension = Path.GetExtension(baseName).ToLower().TrimStart('.');
                        break;
                    case "tar.gz":
                        extension = Path.GetExtension(Path.GetFileNameWithoutExtension(baseName)).ToLower().TrimStart('.');
                        break;
                    default:
                        // No compression extension, use the original extension
                        break;
                }

                // Determine file type based on the final extension
                switch (extension)
                {
                    case "ova":
                        return "OVA";
                    case "iso":
                        return "ISO";
                    case "vmdk":
                        return "VMDK";
                    case "vhd":
                        return "VHD";
                    case "vhdx":
                        return "VHDX";
                    case "qcow2":
                        return "QCOW2";
                    default:
                        return "Other";
                }
            }
        }

        public static async Task<List<GalleryItem>> LoadJsonFromUrl(string url)
        {
            try
            {
                //WriteLog($"Downloading JSON from {url}");
                using (HttpClient client = new HttpClient())
                {
                    string json = await client.GetStringAsync(url);
                    var items = ParseJson(json);
                    //WriteLog($"Parsed JSON from {url}");
                    return items;
                }
            }
            catch (Exception ex)
            {
                //WriteLog($"Failed to download or parse JSON from {url}: {ex.Message}");
                return new List<GalleryItem>();
            }
        }

        public static List<GalleryItem> LoadJsonFromFiles(string path)
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
                //WriteLog($"Failed to parse JSON from {path}: {ex.Message}");
            }
            return items;
        }

        public static List<GalleryItem> LoadJsonFromFile(string path)
        {
            var items = new List<GalleryItem>();
            try
            {
                //WriteLog($"Parsing local JSON file: {path}");
                string json = File.ReadAllText(path);
                items.AddRange(ParseJson(json));
                //WriteLog($"Parsed JSON from {path}");
            }
            catch (Exception ex)
            {
                //WriteLog($"Failed to parse JSON from {path}: {ex.Message}");
            }
            return items;
        }

        public static async Task<List<GalleryItem>> LoadXmlFromUrl(string url)
        {
            var items = new List<GalleryItem>();
            try
            {
                //WriteLog($"Downloading XML from {url}");
                using (HttpClient client = new HttpClient())
                {
                    string xml = await client.GetStringAsync(url);
                    var xdoc = XDocument.Parse(xml);
                    var images = new List<Dictionary<string, object>>();

                    var vhd = xdoc.Element("vhd");
                    if (vhd == null)
                    {
                       // WriteLog("No vhd element found in XML");
                        return items;
                    }

                    var details = vhd.Element("details");
                    if (details == null)
                    {
                       // WriteLog("No details element found in XML");
                        return items;
                    }

                    var descriptions = vhd.Element("descriptions")?.Elements("description").Select(d => d.Value).ToList() ?? new List<string>();
                    var image = vhd.Element("image");

                    if (image == null)
                    {
                        //WriteLog("No image element found in XML");
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
                    //WriteLog($"Parsed XML as JSON from {url}");
                }
            }
            catch (Exception ex)
            {
               //WriteLog($"Failed to download or parse XML from {url}: {ex.Message}");
            }
            return items;
        }

        private static List<GalleryItem> ParseJson(string json)
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
                            //WriteLog("Skipping image: Missing critical keys (name or disk.uri)");
                            continue;
                        }

                        string name = nameProp.GetString();
                        string diskUri = diskUriProp.GetString();
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(diskUri))
                        {
                           // WriteLog("Skipping image: Empty name or diskUri");
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
                        //WriteLog($"Error parsing image: Missing key '{ex.Message}'");
                    }
                    catch (Exception ex)
                    {
                       // WriteLog($"Error parsing image: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
               // WriteLog($"Error parsing JSON: {ex.Message}");
            }
            return items;
        }
    }
}