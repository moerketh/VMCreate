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
    /// Returns both the stable point release and the latest weekly build.
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

            var items = new List<GalleryItem>();

            // ── Stable point release (required) ──
            var stable = await LoadStableReleaseAsync(client, cancellationToken);
            if (stable != null)
            {
                items.Add(stable);
                var stableKde = CreateKdeTwin(stable);
                if (stableKde != null)
                    items.Add(stableKde);
            }

            // ── Latest weekly build (best-effort) ──
            var weekly = await LoadWeeklyReleaseAsync(client, cancellationToken);
            if (weekly != null)
            {
                items.Add(weekly);
                var weeklyKde = CreateKdeTwin(weekly);
                if (weeklyKde != null)
                    items.Add(weeklyKde);
            }

            if (items.Count == 0)
                throw new Exception("Could not find any Kali Linux Hyper-V images.");

            return items;
        }

        /// <summary>
        /// Fetches the stable point release from <c>/current/</c>.
        /// Version format: <c>2026.2</c> (year.quarter).
        /// </summary>
        private async Task<GalleryItem?> LoadStableReleaseAsync(HttpClient client, CancellationToken cancellationToken)
        {
            var response = await client.GetAsync(StableBaseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var baseUrl = response.RequestMessage!.RequestUri!
                .GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";

            var match = HyperVRegex.Match(html);
            if (!match.Success)
                return null;

            var fileName = match.Groups[1].Value;
            var date = match.Groups[4].Value;

            string version = "Unknown";
            var versionMatch = StableVersionRegex.Match(fileName);
            if (versionMatch.Success)
                version = versionMatch.Groups[1].Value;

            return new GalleryItem
            {
                Name = $"Kali Linux {version}",
                Description = $"Kali Linux Hyper-V Image ({fileName}) - Released: {date}",
                Publisher = Publisher,
                DiskUri = baseUrl + fileName,
                SymbolUri = SymbolUrl,
                LastUpdated = ParseDate(date),
                Version = version,
                Category = "Security",
                IsRecommended = true,
                LinuxDistro = LinuxDistro.Kali,
                // Kali's Hyper-V images ship a uid-1000 desktop user named
                // 'kali' (toor is the legacy root alias). InstallLamcoRdpStep
                // substitutes this into the root-run install script and
                // EnableGraphicalAutologinStep needs it to configure the DM —
                // a blank field previously collapsed every user-unit path
                // (TEST_20260910165003: units written to /.config).
                InitialUsername = "kali"
            };
        }

        /// <summary>
        /// Fetches the latest weekly build from <c>/kali-weekly/</c>.
        /// The directory accumulates multiple weeks; we pick the highest week number.
        /// Version format: <c>2026-W26</c> (ISO year-week).
        /// </summary>
        private async Task<GalleryItem?> LoadWeeklyReleaseAsync(HttpClient client, CancellationToken cancellationToken)
        {
            try
            {
                var response = await client.GetAsync(WeeklyBaseUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken);

                var baseUrl = response.RequestMessage!.RequestUri!
                    .GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";

                // The weekly page lists multiple weeks — find all and pick the latest.
                var matches = HyperVRegex.Matches(html);
                if (matches.Count == 0)
                    return null;

                string? bestFileName = null;
                string? bestVersion = null;
                int bestYear = 0, bestWeek = 0;
                string? bestDate = null;

                foreach (Match m in matches)
                {
                    var fileName = m.Groups[1].Value;
                    var date = m.Groups[4].Value;

                    var vm = WeeklyVersionRegex.Match(fileName);
                    if (!vm.Success)
                        continue;

                    int year = int.Parse(vm.Groups[1].Value, CultureInfo.InvariantCulture);
                    int week = int.Parse(vm.Groups[2].Value, CultureInfo.InvariantCulture);

                    if (year > bestYear || (year == bestYear && week > bestWeek))
                    {
                        bestYear = year;
                        bestWeek = week;
                        bestFileName = fileName;
                        bestVersion = $"{year}-W{week:D2}";
                        bestDate = date;
                    }
                }

                // The three best* locals are always assigned together (same if-block),
                // so a single guard proves all three non-null to the compiler.
                if (bestFileName == null || bestVersion == null || bestDate == null)
                    return null;

                return new GalleryItem
                {
                    Name = $"Kali Linux Weekly ({bestVersion})",
                    Description = $"Kali Linux weekly Hyper-V Image — untested build with the latest updates ({bestFileName}) - Released: {bestDate}",
                    Publisher = Publisher,
                    DiskUri = baseUrl + bestFileName,
                    SymbolUri = SymbolUrl,
                    LastUpdated = ParseDate(bestDate),
                    Version = bestVersion,
                    Category = "Security",
                    IsRecommended = false,
                    LinuxDistro = LinuxDistro.Kali,
                    InitialUsername = "kali"
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Graceful degradation: weekly is best-effort, stable is the primary item.
                return null;
            }
        }

        /// <summary>
        /// Creates the KDE Plasma twin of a Kali item: same disk image,
        /// same release, but tagged <c>kali-kde</c> so the
        /// <c>KaliKdeSwitchStep</c> post-boot customization swaps the stock
        /// XFCE desktop for KDE Plasma (and, under the Auto RDP backend,
        /// the Wayland-default Plasma session steers detection to the
        /// Lamco RDP Server).
        /// <para>
        /// The twin shares the base item's <c>DiskUri</c> — the desktop is
        /// switched post-boot, it is not a separate download — so the
        /// aggregate loader's (Name, DiskUri) dedupe keeps both: names
        /// differ (the "(KDE)" suffix), disks match.
        /// </para>
        /// </summary>
        private static GalleryItem? CreateKdeTwin(GalleryItem source)
        {
            if (source == null)
                return null;

            return new GalleryItem
            {
                Name = $"{source.Name} (KDE)",
                Description = $"Kali Linux with the KDE Plasma desktop, switched from XFCE after deployment. Same disk image as {source.Name}.",
                Publisher = source.Publisher,
                DiskUri = source.DiskUri,
                SymbolUri = source.SymbolUri,
                LastUpdated = source.LastUpdated,
                Version = source.Version,
                Category = source.Category,
                IsRecommended = false,
                LinuxDistro = LinuxDistro.Kali,
                // Same desktop user as the base item — the twin shares the
                // disk, so it shares the login account.
                InitialUsername = source.InitialUsername,
                Tags = new List<string> { "kali-kde" }
            };
        }

        private static string ParseDate(string date)
            => DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                ? parsedDate.ToLongDateString()
                : DateTime.Now.ToLongDateString();
    }
}
