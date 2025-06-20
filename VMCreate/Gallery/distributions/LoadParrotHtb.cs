using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class LoadParrotHtb : IGalleryLoader
    {
        private const string BaseUrl = "https://deb.parrot.sh/parrot/iso/6.3.2/";
        private const string LogoUri = "https://www.parrotsec.org/images/parrot-logo.png";
        private const string Thumbnail = "https://parrotsec.org/_next/static/media/htb-1.14502f45.png";
        private const string SymbolUri = "https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTWb4XCIcMpF6J3-37LaMWassk71PPNVWU7Qw&s";
        private readonly IHttpClientFactory _clientFactory;

        public LoadParrotHtb(IHttpClientFactory clientFactory)
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

            var pattern = @"<a href=""(Parrot-htb-[\d\.]+_amd64\.iso)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)";
            var match = Regex.Match(htmlContent, pattern, RegexOptions.Singleline);

            if (!match.Success)
            {
                throw new Exception("Could not find HTB Edition file.");
            }

            var filename = match.Groups[1].Value;
            var dateStr = match.Groups[2].Value;

            var versionPattern = @"Parrot-htb-([\d\.]+)_amd64\.iso";
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
                Name = "Parrot HTB Edition",
                Publisher = "Parrot Project",
                Description = $"Parrot OS HTB Edition, customized for Hack The Box enthusiasts with additional hacking tools and resources (version {version})",
                ThumbnailUri = Thumbnail,
                LogoUri = LogoUri,
                SymbolUri = SymbolUri,
                DiskUri = downloadUrl,
                ArchiveRelativePath = null,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = version,
                LastUpdated = lastUpdated.ToString("o")
            };
            return new List<GalleryItem> { galleryItem };
        }
    }
}