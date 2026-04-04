using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery.distributions
{
    public class PwnCloudOS : IGalleryLoader
    {
        private const string TemplateUrl = "https://download.pwncloudos.pwnedlabs.io/images/pwncloudos-amd64.ova";
        private const string ChecksumPageUrl = "https://pwncloudos.pwnedlabs.io/";
        private const string SymbolUri = "https://pwncloudos.pwnedlabs.io/hubfs/pwnedlabs-notagline.svg";
        private const string ThumbnailUri = "https://pwncloudos.pwnedlabs.io/hubfs/image-1.png";

        private readonly IHttpClientFactory _clientFactory;

        public PwnCloudOS(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var lastModified = DateTime.UtcNow;

            var galleryItem = new GalleryItem
            {
                Name = "PwnCloudOS",
                Publisher = "Pwned Labs",
                Description = $"The multi-cloud security platform for hackers and defenders.",
                SymbolUri = SymbolUri,
                ThumbnailUri = ThumbnailUri,
                DiskUri = TemplateUrl,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                LastUpdated = lastModified.ToString("o"),
                InitialUsername = "pwnedlabs",
                InitialPassword = "pwnedlabs",
                Category = "Security",
                IsRecommended = true
            };

            // Best-effort: fetch the published SHA256 from the download page
            try
            {
                var client = _clientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);
                client.Timeout = TimeSpan.FromSeconds(15);

                var html = await client.GetStringAsync(ChecksumPageUrl, cancellationToken);

                // The checksums page has a table row like:
                //   AMD64 Virtual Machine | 2f6b12183003bee17066fe8066acc2f4af74262456828018197647420360dd55
                var match = Regex.Match(html,
                    @"AMD64\s+Virtual\s+Machine[^a-fA-F0-9]*([a-fA-F0-9]{64})",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                if (match.Success)
                {
                    galleryItem.Checksum = match.Groups[1].Value.ToLowerInvariant();
                    galleryItem.ChecksumAlgorithm = "SHA256";
                }
            }
            catch
            {
                // Website unreachable, HTML format changed, or token cancelled — continue without checksum
            }

            return new List<GalleryItem> { galleryItem };
        }
    }
}