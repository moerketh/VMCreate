using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class LoadKaliCurrent : IGalleryLoader
    {
        private const string BaseUrl = "https://cdimage.kali.org/current/";
        private const string SymbolUri = "https://www.kali.org/blog/kali-linux-2022-4-release/images/kali-logo-dragon-blue-transparent.png";
        private const string LogoUrl = "https://pkg.kali.org/static/img/kali-logo.png";
        private readonly IHttpClientFactory _clientFactory;

        public LoadKaliCurrent(IHttpClientFactory clientFactory)
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

            var baseUri = response.RequestMessage.RequestUri;
            var baseUrl = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";

            var regex = new Regex(@"<a href=""(kali-linux-[^""]*-hyperv-amd64\.7z)"".*?>(.*?)</a>.*?<td class=""size"">([^<]+)</td>.*?<td class=""date"">([^<]+)</td>", RegexOptions.Singleline);
            var match = regex.Match(htmlContent);

            if (!match.Success)
            {
                throw new Exception("Could not find Kali Linux Hyper-V image in the directory listing.");
            }

            var fileName = match.Groups[1].Value;
            var fileSize = match.Groups[3].Value;
            var date = match.Groups[4].Value;
            var downloadUrl = baseUrl + fileName;

            // Extract version from fileName (e.g., kali-linux-2024.3-hyperv-amd64.7z -> 2024.3)
            string version = "Unknown";
            var versionMatch = Regex.Match(fileName, @"kali-linux-(\d+\.\d+)-hyperv-amd64\.7z");
            if (versionMatch.Success)
            {
                version = versionMatch.Groups[1].Value;
            }

            // Create GalleryItem
            var galleryItem = new GalleryItem
            {
                Name = $"Kali Linux {version}",
                Description = $"Kali Linux Hyper-V Image ({fileName}) - Released: {date}",
                Publisher = "OffSec Services Limited",
                DiskUri = downloadUrl,
                LogoUri = LogoUrl,
                SymbolUri = SymbolUri,
                LastUpdated = (DateTime.TryParse(date, out var parsedDate) ? parsedDate : DateTime.Now).ToLongDateString(),
                Version = version
            };

            return new List<GalleryItem> { galleryItem };
        }
    }
}
