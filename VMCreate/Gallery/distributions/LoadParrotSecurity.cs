using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class LoadParrotSecurity : IGalleryLoader
    {
        private const string BaseUrl = "https://deb.parrot.sh/parrot/iso/6.3.2/";
        private const string Thumbnail = "https://parrotsec.org/_next/static/media/parrot-security-1.c044d5dd.png";
        private const string LogoUri = "https://www.parrotsec.org/images/parrot-logo.png";
        private const string SymbolUri = "https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTWb4XCIcMpF6J3-37LaMWassk71PPNVWU7Qw&s";
        private readonly IHttpClientFactory _clientFactory;

        public LoadParrotSecurity(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

            var response = await client.GetAsync(BaseUrl);
            response.EnsureSuccessStatusCode();
            var htmlContent = await response.Content.ReadAsStringAsync();

            var preferredPattern = @"<a href=""(Parrot-security-[\d\.]+_amd64\.vmdk\.xz)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)";
            var match = Regex.Match(htmlContent, preferredPattern, RegexOptions.Singleline);

            if (!match.Success)
            {
                var fallbackPattern = @"<a href=""(Parrot-security-[\d\.]+_amd64\.iso)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)";
                match = Regex.Match(htmlContent, fallbackPattern, RegexOptions.Singleline);
            }

            if (!match.Success)
            {
                throw new Exception("Could not find Security Edition file.");
            }

            var filename = match.Groups[1].Value;
            var dateStr = match.Groups[2].Value;

            var versionPattern = @"Parrot-security-([\d\.]+)_amd64\.(vmdk\.xz|iso)";
            var versionMatch = Regex.Match(filename, versionPattern);
            if (!versionMatch.Success)
            {
                throw new Exception($"Could not extract version from filename: {filename}");
            }
            var version = versionMatch.Groups[1].Value;

            var lastUpdated = DateTime.ParseExact(dateStr, "dd-MMM-yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

            var downloadUrl = BaseUrl + filename;

            var galleryItem =  new GalleryItem
            {
                Name = "Parrot Security OS",
                Publisher = "Parrot Project",
                Description = $"Parrot Security OS, includes a full set of penetration testing tools (version {version})",
                ThumbnailUri = Thumbnail,
                LogoUri = LogoUri,
                SymbolUri = SymbolUri,
                DiskUri = downloadUrl,
                ArchiveRelativePath = filename.EndsWith(".xz", StringComparison.InvariantCultureIgnoreCase) ? Path.GetFileNameWithoutExtension(filename) : filename,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = version,
                LastUpdated = lastUpdated.ToString("o")
            };
            return new List<GalleryItem> { galleryItem };
        }
    }
}