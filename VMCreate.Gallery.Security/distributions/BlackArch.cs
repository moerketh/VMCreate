using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public class BlackArch : IGalleryLoader
    {
        private const string DownloadsUrl = "https://www.blackarch.org/downloads.html";
        private readonly IHttpClientFactory _clientFactory;
        private const string PinnedVersion = "2023.04.01";

        public BlackArch(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(BlackArch).Assembly, "blackarch-logo.png");
            var galleryItems = new List<GalleryItem>();
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

            // Fetch the downloads page
            var response = await client.GetAsync(DownloadsUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var htmlContent = await response.Content.ReadAsStringAsync();

                // Simplified regex for Slim ISO URL
                string slimPattern = @"<a href=""(https:\/\/[^""]+blackarch-linux-slim-2023\.05\.01-x86_64\.iso)""";
                string ovaPattern = @"<a href=""(https:\/\/[^""]+blackarch-linux-2023\.04\.01\.ova)""";

                // Find Slim ISO
                var slimMatch = Regex.Match(htmlContent, slimPattern);
                if (slimMatch.Success)
                {
                    var slimUrl = slimMatch.Groups[1].Value;

                    var slimItem = new GalleryItem
                    {
                        Name = "BlackArch Linux 64 bit Slim ISO",
                        Publisher = "BlackArch Project",
                        Description = $"BlackArch Linux is an Arch Linux-based penetration testing distribution for penetration testers and security researchers. This is the Slim ISO (version {PinnedVersion}), which contains a functional BlackArch Linux system with a selected set of common/well-known tools and system utilities for pentesting.",
                        ThumbnailUri = "https://blackarch.org/images/screenshots/menu_slim.png",
                        SymbolUri = logoUri,
                        LogoUri = logoUri,
                        DiskUri = slimUrl,
                        ArchiveRelativePath = null,
                        SecureBoot = "false",
                        EnhancedSessionTransportType = "HvSocket",
                        Version = PinnedVersion,
                        LastUpdated = DateTime.ParseExact(PinnedVersion, "yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture).ToString("o"),
                        Category = "Security"
                    };
                    galleryItems.Add(slimItem);
                }
                else
                {
                    throw new Exception("Could not find BlackArch Slim ISO URL in the downloads page.");
                }

                // Find OVA Image
                var ovaMatch = Regex.Match(htmlContent, ovaPattern);
                if (ovaMatch.Success)
                {
                    var ovaUrl = ovaMatch.Groups[1].Value;

                    var ovaItem = new GalleryItem
                    {
                        Name = "BlackArch Linux 64 bit OVA Image",
                        Publisher = "BlackArch Project",
                        Description = $"BlackArch Linux is an Arch Linux-based penetration testing distribution for penetration testers and security researchers. This is the OVA Image (version {PinnedVersion}), suitable for running in VirtualBox, VMware, and QEMU.",
                        ThumbnailUri = "https://blackarch.org/images/screenshots/menu_slim.png",
                        SymbolUri = logoUri,
                        LogoUri = logoUri,
                        DiskUri = ovaUrl,
                        ArchiveRelativePath = "blackarch-disk001.vmdk",
                        SecureBoot = "false",
                        EnhancedSessionTransportType = "HvSocket",
                        Version = PinnedVersion,
                        LastUpdated = DateTime.ParseExact(PinnedVersion, "yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture).ToString("o"),
                        InitialUsername = "root",
                        InitialPassword = "blackarch",
                        Category = "Security"
                    };
                    galleryItems.Add(ovaItem);
                }
                else
                {
                    throw new Exception("Could not find BlackArch OVA Image URL in the downloads page.");
                }
            return galleryItems;
        }
    }
}