using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class ClearLinux : IGalleryLoader
    {
        private const string BaseUrl = "https://cdn.download.clearlinux.org/releases/current/clear/";
        private const string Thumbnail = "https://raw.githubusercontent.com/clearlinux/clearlinux.github.io/refs/heads/main/sites/default/files/clear-desktop.PNG";
        private const string LogoUri = "https://raw.githubusercontent.com/clearlinux/clearlinux.github.io/refs/heads/main/sites/default/files/ClearLinuxProject_logo_primary_dark_1.png";
        private const string SymbolUri = "https://www.clearlinux.org/sites/default/files/2017-12/clearlinux-logo.svg";
        private readonly IHttpClientFactory _clientFactory;

        public ClearLinux(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

            var response = await client.GetAsync(BaseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var htmlContent = await response.Content.ReadAsStringAsync();

            var preferredPattern = @"<a href=""(clear-(\d+)-live-desktop\.iso)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})";
            var match = Regex.Match(htmlContent, preferredPattern, RegexOptions.Singleline);

            if (!match.Success)
            {
                throw new Exception("Could not find live desktop ISO file.");
            }

            var filename = match.Groups[1].Value;
            var dateStr = match.Groups[3].Value;
            var version = match.Groups[2].Value;

            var lastUpdated = DateTime.ParseExact(dateStr, "dd-MMM-yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

            var downloadUrl = BaseUrl + filename;

            var galleryItem = new GalleryItem
            {
                Name = "Clear Linux Desktop",
                Publisher = "Intel Corporation",
                Description = $"Clear Linux OS with live desktop environment, optimized for performance and security.",
                ThumbnailUri = Thumbnail,
                LogoUri = LogoUri,
                SymbolUri = SymbolUri,
                DiskUri = downloadUrl,
                ArchiveRelativePath = filename,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = version,
                LastUpdated = lastUpdated.ToString("o")
            };
            return new List<GalleryItem> { galleryItem };
        }
    }
}