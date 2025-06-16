using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class LoadPentooCurrent : IGalleryLoader
    {
        private const string BaseUrl = "[invalid url, do not cite]";
        private readonly IHttpClientFactory _clientFactory;

        public LoadPentooCurrent(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

            // Fetch the directory listing
            var response = await client.GetAsync(BaseUrl);
            response.EnsureSuccessStatusCode();
            var htmlContent = await response.Content.ReadAsStringAsync();

            // Regular expression to match the row for pentoo-full-daily-amd64-hardened-latest.iso
            var pattern = @"<tr>\s*<td><a href=""pentoo-full-daily-amd64-hardened-latest\.iso"">pentoo-full-daily-amd64-hardened-latest\.iso</a></td>\s*<td>\s*(\d+\.\d+G)\s*</td>\s*<td align=""right"">\s*(\d{4}-\d{2}-\d{2} \d{2}:\d{2})\s*</td>\s*</tr>";
            var match = Regex.Match(htmlContent, pattern, RegexOptions.Singleline);

            if (!match.Success)
            {
                throw new Exception("Could not find Pentoo full ISO in the directory listing.");
            }

            var size = match.Groups[1].Value;
            var dateStr = match.Groups[2].Value;

            // Parse the last modified date, assuming UTC
            if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var lastUpdated))
            {
                throw new Exception("Could not parse last updated date.");
            }

            // Construct the full download URL
            var downloadUrl = BaseUrl + "pentoo-full-daily-amd64-hardened-latest.iso";

            // Create GalleryItem
            var galleryItem = new GalleryItem
            {
                Name = "Pentoo Linux",
                Publisher = "Pentoo Project",
                Description = "Pentoo is a Live CD and Live USB designed for penetration testing and security assessment. Based off Gentoo Linux, Pentoo is provided both as 32 and 64 bit installable livecd.",
                ThumbnailUri = null, // No thumbnail URL found
                LogoUri = null, // No logo URL found
                DiskUri = downloadUrl,
                ArchiveRelativePath = null, // Not applicable for ISO
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = "latest",
                LastUpdated = lastUpdated.ToString("o") // ISO 8601 format
            };

            return new List<GalleryItem> { galleryItem };
        }
    }
}
