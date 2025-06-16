using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class LoadParrotCurrent : IGalleryLoader
    {
        private const string BaseUrl = "https://deb.parrot.sh/parrot/iso/current/";
        private readonly IHttpClientFactory _clientFactory;
        private const string Thumbnail = "https://parrotsec.org/_next/static/media/parrot-security-1.c044d5dd.png";
        public LoadParrotCurrent(IHttpClientFactory clientFactory)
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

            // Regular expression to match the Security Edition ISO for amd64
            var pattern = @"<a href=""(Parrot-security-[\d\.]+_amd64\.iso)"">.*?</a>.*?<td class=""size"">(\d+\.\d+G)</td>.*?<td class=""date"">(\d{4}-\d{2}-\d{2} \d{2}:\d{2})</td>";
            var match = Regex.Match(htmlContent, pattern, RegexOptions.Singleline);

            if (!match.Success)
            {
                throw new Exception("Could not find Parrot Security ISO in the directory listing.");
            }

            var fileName = match.Groups[1].Value;
            var size = match.Groups[2].Value;
            var dateStr = match.Groups[3].Value;

            // Extract version from filename
            var versionMatch = Regex.Match(fileName, @"Parrot-security-[\d\.]+_amd64\.iso");
            if (!versionMatch.Success)
            {
                throw new Exception("Could not extract version from ISO filename.");
            }
            var version = versionMatch.Groups[1].Value;

            // Parse the last modified date, assuming UTC
            if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var lastUpdated))
            {
                throw new Exception("Could not parse last updated date.");
            }

            // Construct the full download URL
            var downloadUrl = BaseUrl + fileName;

            // Create GalleryItem
            var galleryItem = new GalleryItem
            {
                Name = "Parrot Security OS",
                Publisher = "Parrot Project",
                Description = $"Parrot Security OS is a Debian-based Linux distribution focused on security, privacy, and development. This is the Security Edition (version {version}), which includes a full set of penetration testing tools.",
                ThumbnailUri = Thumbnail,
                LogoUri = "https://www.parrotsec.org/images/parrot-logo.png",
                DiskUri = downloadUrl,
                ArchiveRelativePath = null, // Not applicable for ISO
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = version,
                LastUpdated = lastUpdated.ToString("o") // ISO 8601 format
            };

            return new List<GalleryItem> { galleryItem };
        }
    }
}