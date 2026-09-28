using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Gallery loader for Kali Linux Hyper-V images.
    /// Returns a single item whose <see cref="GalleryItem.ReleaseVariants"/>
    /// list offers the stable point release (default) and the latest weekly
    /// build; the release is chosen on the VM settings page or via the CLI
    /// <c>--release</c> flag.
    /// </summary>
    public class Kali : IGalleryLoader
    {
        private const string StableBaseUrl = "https://cdimage.kali.org/current/";
        private const string WeeklyBaseUrl = "https://cdimage.kali.org/kali-weekly/";
        private const string SymbolUrl = "https://www.kali.org/images/kali-logo.svg";
        private const string Publisher = "OffSec Services Limited";

        private static readonly Regex HyperVRegex = new(
            @"<a href=""(kali-linux-[^""]*-hyperv-amd64\.7z)"".*?>(.*?)</a>.*?<td class=""size"">([^<]+)</td>.*?<td class=""date"">([^<]+)</td>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex StableVersionRegex = new(
            @"kali-linux-(\d+\.\d+)-hyperv-amd64\.7z",
            RegexOptions.Compiled);

        private static readonly Regex WeeklyVersionRegex = new(
            @"kali-linux-(\d+)-W(\d+)-hyperv-amd64\.7z",
            RegexOptions.Compiled);

        private readonly IHttpClientFactory _clientFactory;

        public Kali(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);

            // ── Stable point release (required — the item itself describes it) ──
            var stable = await ScrapeStableAsync(client, cancellationToken)
                         ?? throw new Exception("Could not find any Kali Linux Hyper-V images.");

            var variants = new List<ReleaseVariant>
            {
                new()
                {
                    Id = "stable",
                    DisplayName = $"Stable ({stable.Version})",
                    Description = "Fully-tested point release (quarterly).",
                    DiskUri = stable.DiskUri,
                    ChecksumUri = stable.ChecksumUri,
                    Version = stable.Version,
                    LastUpdated = ParseDate(stable.DateText),
                    DownloadSizeText = stable.SizeText,
                    IsDefault = true
                }
            };

            // ── Latest weekly build (best-effort) ──
            var weekly = await TryScrapeWeeklyAsync(client, cancellationToken);
            if (weekly != null)
            {
                variants.Add(new ReleaseVariant
                {
                    Id = "weekly",
                    DisplayName = $"Weekly ({weekly.Version})",
                    Description = "Untested build with the latest updates.",
                    DiskUri = weekly.DiskUri,
                    ChecksumUri = weekly.ChecksumUri,
                    Version = weekly.Version,
                    LastUpdated = ParseDate(weekly.DateText),
                    DownloadSizeText = weekly.SizeText,
                    IsDefault = false
                });
            }

            return new List<GalleryItem>
            {
                new()
                {
                    Name = "Kali Linux",
                    Description = "Kali Linux Hyper-V image — stable point release plus untested weekly builds with the latest updates.",
                    Publisher = Publisher,
                    DiskUri = stable.DiskUri,
                    // The item's own fields describe the stable release;
                    // ResolveRelease swaps in the chosen variant's checksum.
                    ChecksumUri = stable.ChecksumUri,
                    ChecksumAlgorithm = "SHA256",
                    SymbolUri = SymbolUrl,
                    LastUpdated = ParseDate(stable.DateText),
                    Version = stable.Version,
                    Category = "Security",
                    IsRecommended = true,
                    LinuxDistro = LinuxDistro.Kali,
                    // Kali's Hyper-V images ship a uid-1000 desktop user named
                    // 'kali' (toor is the legacy root alias). InstallLamcoRdpStep
                    // substitutes this into the root-run install script and
                    // EnableGraphicalAutologinStep needs it to configure the DM —
                    // a blank field previously collapsed every user-unit path
                    // (TEST_20260910165003: units written to /.config).
                    InitialUsername = "kali",
                    ReleaseVariants = variants
                }
            };
        }

        /// <summary>
        /// One scraped row of a cdimage directory listing.
        /// </summary>
        private sealed record ScrapedRelease(string DiskUri, string ChecksumUri, string Version, string DateText, string SizeText);

        /// <summary>
        /// Fetches the stable point release from <c>/current/</c>.
        /// Version format: <c>2026.2</c> (year.quarter). Returns null when the
        /// listing has no Hyper-V image; HTTP failures propagate.
        /// </summary>
        private async Task<ScrapedRelease?> ScrapeStableAsync(HttpClient client, CancellationToken cancellationToken)
        {
            var response = await client.GetAsync(StableBaseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var baseUrl = response.RequestMessage!.RequestUri!
                .GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";

            var match = HyperVRegex.Match(html);
            if (!match.Success)
                return null;

            return FromMatch(match, baseUrl, StableVersionRegex,
                m => m.Groups[1].Value);
        }

        /// <summary>
        /// Fetches the latest weekly build from <c>/kali-weekly/</c>.
        /// The directory accumulates multiple weeks; we pick the highest week
        /// number. Version format: <c>2026-W26</c> (ISO year-week). Returns
        /// null on any failure — weekly is best-effort.
        /// </summary>
        private async Task<ScrapedRelease?> TryScrapeWeeklyAsync(HttpClient client, CancellationToken cancellationToken)
        {
            try
            {
                var response = await client.GetAsync(WeeklyBaseUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken);

                var baseUrl = response.RequestMessage!.RequestUri!
                    .GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";

                // The weekly page lists multiple weeks — find all and pick the latest.
                var candidates = new List<(int Year, int Week, Match Match)>();
                foreach (Match m in HyperVRegex.Matches(html))
                {
                    var vm = WeeklyVersionRegex.Match(m.Groups[1].Value);
                    if (!vm.Success)
                        continue;

                    candidates.Add((int.Parse(vm.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(vm.Groups[2].Value, CultureInfo.InvariantCulture), m));
                }

                if (candidates.Count == 0)
                    return null;

                var best = candidates.OrderByDescending(c => c.Year).ThenByDescending(c => c.Week).First();
                return FromMatch(best.Match, baseUrl, WeeklyVersionRegex,
                    m => $"{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)}-W{int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture):D2}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Graceful degradation: weekly is best-effort, stable is the item.
                return null;
            }
        }

        private static ScrapedRelease FromMatch(
            Match match, string baseUrl, Regex versionRegex, Func<Match, string> formatVersion)
        {
            var fileName = match.Groups[1].Value;
            var versionMatch = versionRegex.Match(fileName);

            return new ScrapedRelease(
                DiskUri: baseUrl + fileName,
                // cdimage.kali.org publishes a GNU-format SHA256SUMS in the
                // same directory as the image; ChecksumVerifier looks the
                // image filename up in it. Stable and weekly live in
                // different directories, so each release carries its own.
                ChecksumUri: baseUrl + "SHA256SUMS",
                Version: versionMatch.Success ? formatVersion(versionMatch) : "Unknown",
                DateText: match.Groups[4].Value,
                SizeText: match.Groups[3].Value.Trim());
        }

        private static string ParseDate(string date)
            => DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                ? parsedDate.ToLongDateString()
                : DateTime.Now.ToLongDateString();
    }
}
