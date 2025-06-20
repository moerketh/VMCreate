using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class LoadBlackArchCurrent : IGalleryLoader
    {
        private const string BaseUrl = "https://distro.ibiblio.org/blackarch/iso/";
        private readonly IHttpClientFactory _clientFactory;

        public LoadBlackArchCurrent(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var galleryItem = new GalleryItem();
            try
            {
                var client = _clientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

                // Fetch the directory listing
                var response = await client.GetAsync(BaseUrl);
                response.EnsureSuccessStatusCode();
                var htmlContent = await response.Content.ReadAsStringAsync();

                // Regular expression to match rows for Full ISO (x86_64)
                string pattern = @"<tr>\s*<td class=""n""><a href=""(blackarch-linux-full-\d{4}\.\d{2}\.\d{2}-x86_64\.iso)"">\1</a></td>\s*<td class=""m"">(\d{4}-[A-Za-z]{3}-\d{2} \d{2}:\d{2}:\d{2})</td>\s*<td class=""s"">(\d+\.\d+G)</td>\s*<td class=""t"">application/x-iso9660-image</td>\s*</tr>"; 
                var matches = Regex.Matches(htmlContent, pattern, RegexOptions.Singleline);

                if (matches.Count == 0)
                {
                    throw new Exception("Could not find BlackArch Full ISO in the directory listing.");
                }

                // Convert MatchCollection to a list for LINQ operations
                var matchList = matches.Cast<Match>().ToList();

                // Sort matches by filename (which includes the version date) and select the latest
                var sortedMatches = matchList.OrderBy(m => m.Groups[1].Value).ToList();
                var latestMatch = sortedMatches.Last();

                var fileName = latestMatch.Groups[1].Value;
                var dateStr = latestMatch.Groups[2].Value;
                var size = latestMatch.Groups[3].Value;

                // Extract version from filename
                var versionPattern = @"blackarch-linux-full-(\d{4}\.\d{2}\.\d{2})-x86_64\.iso";
                var versionMatch = Regex.Match(fileName, versionPattern);
                if (!versionMatch.Success)
                {
                    throw new Exception("Could not extract version from ISO filename.");
                }
                var version = versionMatch.Groups[1].Value;

                // Parse the last modified date, assuming UTC
                if (!DateTime.TryParseExact(dateStr, "yyyy-MMM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var lastUpdated))
                {
                    throw new Exception("Could not parse last updated date.");
                }

                // Construct the full download URL
                var downloadUrl = BaseUrl + fileName;

                // Create GalleryItem
                galleryItem = new GalleryItem
                {
                    Name = "BlackArch Linux",
                    Publisher = "BlackArch Project",
                    Description = $"BlackArch Linux is an Arch Linux-based penetration testing distribution for penetration testers and security researchers. This is the Full ISO (version {version}), which contains a complete, functional BlackArch Linux system with all available tools.",
                    ThumbnailUri = "https://www.blackarch.org/images/screenshots/thumbnails/menu_slim.jpg", // No thumbnail found
                    SymbolUri = "",                    
                    LogoUri = "https://blackarch.org/img/logo.png",
                    DiskUri = downloadUrl,
                    ArchiveRelativePath = null, // Not applicable for ISO
                    SecureBoot = "false",
                    EnhancedSessionTransportType = "HvSocket",
                    Version = version,
                    LastUpdated = lastUpdated.ToString("o") // ISO 8601 format
                };
            }
            catch (Exception ex)
            {
                throw;
            }
            return new List<GalleryItem> { galleryItem };
        }
    }
}