using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Gallery loader for "BlackArch (on Omarchy)" — the Arch Linux + Hyprland
    /// Omarchy base (an AI-native desktop: agent harnesses ship out of the box)
    /// with the BlackArch repositories added post-boot by <c>BlackArchInstallStep</c>
    /// and a curated AI-era security toolset layered on top.
    /// <para>
    /// This is the Omarchy-flavored twin of the plain <c>BlackArch</c> item: both
    /// are "Arch + BlackArch repos", but this entry rides Omarchy's installer ISO
    /// (fully unattended via the native cidata seed) instead of the Arch cloud
    /// qcow2. It therefore keeps Hyprland/greetd as the desktop (the whole point of
    /// Omarchy) and the SSH-based post-boot pipeline, and composes two tags —
    /// <c>omarchy</c> (passwordless-sudo bootstrap, SSH) and <c>blackarch</c>
    /// (BlackArchInstallStep) — so the existing tag-gated steps fire without new
    /// VM-creation plumbing.
    /// </para>
    /// </summary>
    public class BlackArchOmarchy : IGalleryLoader
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/omacom/omarchy/releases/latest";
        private const string IsoBaseUrl = "https://iso.omarchy.org/";
        private const string SymbolUrl = "https://raw.githubusercontent.com/omacom/omarchy/28ceaae70ebac3a0edcc21f2faa77a90dc6d404c/logo.svg";
        private const string Publisher = "BlackArch on Omarchy (unofficial)";

        private readonly IHttpClientFactory _clientFactory;

        public BlackArchOmarchy(IHttpClientFactory clientFactory)
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
                    Name = $"BlackArch on Omarchy {bareVersion}",
                    Publisher = Publisher,
                    Description = "BlackArch Linux on Omarchy — the Arch + Hyprland AI-native desktop with the BlackArch repositories and a curated AI-era security toolset (agentic scanner, LLM red-teaming, web proxy) added post-boot. Keeps Omarchy's Hyprland and built-in agent harnesses; unattended via the native cidata seed.",
                    SymbolUri = SymbolUrl,
                    DiskUri = IsoBaseUrl + $"omarchy-{bareVersion}.iso",
                    ChecksumUri = IsoBaseUrl + $"omarchy-{bareVersion}.iso.sha256",
                    ChecksumAlgorithm = "sha256",
                    SecureBoot = "false",
                    Version = bareVersion,
                    Category = "Security",
                    LinuxDistro = LinuxDistro.Arch,
                    InitialUsername = "omarchy",
                    InitialPassword = "omarchy",
                    HasUnattendedInstallPath = true,
                    // "omarchy" gates the sudo/SSH bootstrap; "blackarch" gates
                    // BlackArchInstallStep. BlackArchDesktopCatalog excludes this
                    // item (it keeps Hyprland rather than switching desktops).
                    Tags = new List<string> { "omarchy", "blackarch" },
                    IsRecommended = false,
                },
            };
        }
    }
}
