using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class ParrotHome : IGalleryLoader
    {
        private const string BaseUrl = "https://deb.parrot.sh/parrot/iso/7.1/";
        private readonly IHttpClientFactory _clientFactory;

        public ParrotHome(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(ParrotHome).Assembly, "parrot-logo.svg");
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

            var response = await client.GetAsync(BaseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var htmlContent = await response.Content.ReadAsStringAsync();

            var items = new List<GalleryItem>();

            // Look for ISO (live installer)
            var isoPattern = @"<a href=""(Parrot-home-[\d\.]+_amd64\.iso)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)";
            var isoMatch = Regex.Match(htmlContent, isoPattern, RegexOptions.Singleline);

            // Look for QCOW2 (pre-installed disk image, converts directly to VHDX for Hyper-V)
            var qcow2Pattern = @"<a href=""(Parrot-home-[\d\.]+_amd64\.qcow2)"">.*?</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)";
            var qcow2Match = Regex.Match(htmlContent, qcow2Pattern, RegexOptions.Singleline);

            if (!isoMatch.Success && !qcow2Match.Success)
            {
                throw new Exception("Could not find Home Edition file.");
            }

            if (isoMatch.Success)
            {
                var filename = isoMatch.Groups[1].Value;
                var version = ExtractVersion(filename);
                var lastUpdated = ParseDate(isoMatch.Groups[2].Value);

                items.Add(new GalleryItem
                {
                    Name = "Parrot Home Edition",
                    Publisher = "Parrot Project",
                    Description = $"Parrot Home Edition ISO installer, for daily use with a focus on privacy and productivity (version {version})",
                    ThumbnailUri = logoUri,
                    LogoUri = logoUri,
                    SymbolUri = logoUri,
                    DiskUri = BaseUrl + filename,
                    ArchiveRelativePath = filename,
                    SecureBoot = "false",
                    EnhancedSessionTransportType = "HvSocket",
                    Version = version,
                    LastUpdated = lastUpdated.ToString("o"),
                    InitialUsername = "user",
                    InitialPassword = "parrot",
                    Category = "Security"
                });
            }

            if (qcow2Match.Success)
            {
                var filename = qcow2Match.Groups[1].Value;
                var version = ExtractVersion(filename);
                var lastUpdated = ParseDate(qcow2Match.Groups[2].Value);

                items.Add(new GalleryItem
                {
                    Name = "Parrot Home Edition (Pre-installed)",
                    Publisher = "Parrot Project",
                    Description = $"Parrot Home Edition pre-installed disk image, for daily use with a focus on privacy and productivity (version {version})",
                    ThumbnailUri = logoUri,
                    LogoUri = logoUri,
                    SymbolUri = logoUri,
                    DiskUri = BaseUrl + filename,
                    ArchiveRelativePath = "",
                    SecureBoot = "false",
                    EnhancedSessionTransportType = "HvSocket",
                    Version = version,
                    LastUpdated = lastUpdated.ToString("o"),
                    InitialUsername = "user",
                    InitialPassword = "parrot",
                    Category = "Security"
                });
            }

            return items;
        }

        private static string ExtractVersion(string filename)
        {
            var versionMatch = Regex.Match(filename, @"Parrot-home-([\d\.]+)_amd64\.");
            if (!versionMatch.Success)
                throw new Exception($"Could not extract version from filename: {filename}");
            return versionMatch.Groups[1].Value;
        }

        private static DateTime ParseDate(string dateStr)
        {
            return DateTime.ParseExact(dateStr, "dd-MMM-yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
        }
    }
}