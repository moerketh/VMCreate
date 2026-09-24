using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Gallery loader for Omarchy (unofficial) — the Arch Linux + Hyprland
    /// distribution from DHH. Loads the latest stable installer ISO from the
    /// official mirror and deploys it fully unattended through Omarchy's native
    /// cidata seed mechanism (see <c>VMCreate.Cidata.CidataSeedBuilder</c>):
    /// a NoCloud-style seed ISO labeled <c>cidata</c> drives the installer's
    /// autoinstall mode — no interactive prompts, and the installer itself
    /// enables sshd for the created user.
    /// </summary>
    public class Omarchy : IGalleryLoader
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/omacom/omarchy/releases/latest";
        private const string IsoBaseUrl = "https://iso.omarchy.org/";
        // Pinned to the quattro branch tip at development time; a floating branch
        // ref would 404 the gallery icon if the upstream branch is ever deleted.
        private const string SymbolUrl = "https://raw.githubusercontent.com/omacom/omarchy/28ceaae70ebac3a0edcc21f2faa77a90dc6d404c/logo.svg";
        private const string Publisher = "Omarchy (unofficial)";

        private readonly IHttpClientFactory _clientFactory;

        public Omarchy(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);

            var response = await client.GetAsync(LatestReleaseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            string version = doc.RootElement.GetProperty("tag_name").GetString()
                ?? throw new Exception("Omarchy latest release has no tag_name.");

            // Tag is "v4.0.4"; the ISO is "omarchy-4.0.4.iso" (no leading v).
            string bareVersion = version.TrimStart('v', 'V');

            return new List<GalleryItem>
            {
                new()
                {
                    Name = $"Omarchy {bareVersion}",
                    Publisher = Publisher,
                    Description = "Omarchy — Arch Linux with the Hyprland Wayland desktop, installed fully unattended via the native cidata seed ISO. The installer creates the user below with sudo and enables SSH.",
                    SymbolUri = SymbolUrl,
                    // Omarchy release ISOs live on the iso.omarchy.org mirror (not as
                    // GitHub release assets) and always follow omarchy-{version}.iso;
                    // a bad tag therefore only surfaces as a download 404 when the VM
                    // deployment starts fetching the ISO.
                    DiskUri = IsoBaseUrl + $"omarchy-{bareVersion}.iso",
                    ChecksumUri = IsoBaseUrl + $"omarchy-{bareVersion}.iso.sha256",
                    ChecksumAlgorithm = "sha256",
                    SecureBoot = "false",
                    Version = bareVersion,
                    Category = "Security",
                    LinuxDistro = LinuxDistro.Arch,
                    // The cidata seed bakes these credentials into the install:
                    // InitialUsername becomes the created sudo user, InitialPassword
                    // its password (hashed with SHA-512 crypt on the seed).
                    InitialUsername = "omarchy",
                    InitialPassword = "omarchy",
                    HasUnattendedInstallPath = true,
                    // Marks the item for Omarchy-only tailoring: the GUI shows
                    // the required passwordless-sudo bootstrap card, and the
                    // post-boot pipeline gates the bootstrap/teardown steps on
                    // this tag.
                    Tags = new List<string> { "omarchy" },
                    IsRecommended = false,
                },
            };
        }
    }
}
