using Microsoft.Extensions.Logging;
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
    /// Gallery loader for Ubuntu desktop images, discovering the currently
    /// supported releases live instead of pinning them:
    /// <list type="bullet">
    ///   <item>distro-info-data's <c>ubuntu.csv</c> supplies the series →
    ///   version/EOL metadata; unreleased and end-of-life series are
    ///   filtered out, so new releases appear without a code change.</item>
    ///   <item>Series with a Hyper-V desktop image on
    ///   <c>partner-images.canonical.com</c> become one-click VHDX cards:
    ///   the GA build (<c>release/current</c>) is the card's own release and
    ///   the refreshed daily build (<c>current</c>) a non-default release
    ///   variant — the same stable/weekly pattern as the Kali loader.</item>
    ///   <item>Supported series without a Hyper-V image (interim releases
    ///   get none) fall back to the desktop ISO on releases.ubuntu.com, like
    ///   the Debian/Mint cards.</item>
    /// </list>
    /// Every image carries its directory's GNU-format SHA256SUMS so the
    /// download pipeline verifies it. A pinned known-good 22.04/24.04 pair
    /// is returned when discovery fails, so a mirror or distro-info
    /// restructure degrades to the historical gallery instead of an empty
    /// card list.
    /// </summary>
    public class Ubuntu : IGalleryLoader
    {
        private const string DesktopBaseUri = "https://partner-images.canonical.com/hyper-v/desktop/";
        private const string DistroInfoUri = "https://git.launchpad.net/ubuntu/+source/distro-info-data/plain/ubuntu.csv";
        private const string ReleasesBaseUri = "https://releases.ubuntu.com/";
        private const string ThumbnailUri = DesktopBaseUri + "bionic/ubuntu_thumbnail.jpg";
        private const string PublisherName = "Canonical Group Ltd";
        private const string BaseDescription =
            "The open source desktop operating system that powers millions of PCs and laptops around the world.";

        private const string PinnedJammyUri = DesktopBaseUri + "jammy/release/current/ubuntu-jammy-hyperv-amd64-ubuntu-desktop-hyperv.vhdx.zip";
        private const string PinnedNobleUri = DesktopBaseUri + "noble/release/current/ubuntu-noble-hyperv-amd64-ubuntu-desktop-hyperv.vhdx.zip";

        /// <summary>Directory links in the mirror's Apache index (excludes files and the parent link).</summary>
        private static readonly Regex SeriesDirRegex = new(@"href=""([a-z]+)/""", RegexOptions.Compiled);

        private readonly ILogger<Ubuntu> _logger;
        private readonly IHttpClientFactory _clientFactory;

        public Ubuntu(ILogger<Ubuntu> logger, IHttpClientFactory clientFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            try
            {
                var discovered = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
                if (discovered.Count > 0)
                    return discovered;

                _logger.LogWarning("Ubuntu image discovery found no releases; falling back to pinned 22.04/24.04 GA images.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ubuntu image discovery failed; falling back to pinned 22.04/24.04 GA images.");
            }

            return PinnedFallbackItems();
        }

        /// <summary>One supported Ubuntu series from distro-info-data.</summary>
        private sealed record SeriesInfo(string Series, string Version, double Number, DateTime Release, DateTime Eol);

        /// <summary>One downloadable image channel (GA or daily) of a mirror series.</summary>
        private sealed record ChannelImage(string DiskUri, string ChecksumUri, string? Serial);

        private async Task<List<GalleryItem>> DiscoverAsync(CancellationToken cancellationToken)
        {
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);

            var csvTask = FetchAsync(client, DistroInfoUri, cancellationToken);
            var indexTask = FetchAsync(client, DesktopBaseUri, cancellationToken);

            var supported = ParseSupportedSeries(await csvTask.ConfigureAwait(false));
            var indexHtml = await indexTask.ConfigureAwait(false);

            var mirrorDirs = SeriesDirRegex.Matches(indexHtml)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            // Task.WhenAll preserves source order — newest series first.
            var tasks = supported
                .Select(s => LoadSeriesAsync(client, s, onMirror: mirrorDirs.Contains(s.Series), cancellationToken))
                .ToList();

            return (await Task.WhenAll(tasks).ConfigureAwait(false))
                .Where(i => i != null)
                .Cast<GalleryItem>()
                .ToList();
        }

        private async Task<GalleryItem?> LoadSeriesAsync(HttpClient client, SeriesInfo series, bool onMirror, CancellationToken cancellationToken)
        {
            if (onMirror)
            {
                var seriesUri = DesktopBaseUri + series.Series + "/";
                var gaTask = TryLoadChannelAsync(client, seriesUri + "release/current/", series.Series, cancellationToken);
                var dailyTask = TryLoadChannelAsync(client, seriesUri + "current/", series.Series, cancellationToken);
                var ga = await gaTask.ConfigureAwait(false);
                var daily = await dailyTask.ConfigureAwait(false);

                if (ga != null || daily != null)
                    return BuildVhdxItem(series, ga, daily);
            }

            return await TryLoadIsoItemAsync(client, series, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Scrapes one channel directory for the series' VHDX zip. The
        /// filename is scraped (not constructed) because it changes between
        /// releases — e.g. resolute moved to <c>amd64v3</c>. The build serial
        /// comes from the same directory's build-info.txt, matching the image
        /// the card actually downloads. Returns null when the directory or
        /// image is absent (404 for a pre-GA series, a dropped series).
        /// </summary>
        private async Task<ChannelImage?> TryLoadChannelAsync(HttpClient client, string dirUri, string series, CancellationToken cancellationToken)
        {
            try
            {
                var html = await FetchAsync(client, dirUri, cancellationToken).ConfigureAwait(false);
                var match = Regex.Match(html, $@"href=""(ubuntu-{Regex.Escape(series)}-hyperv-[^""]+\.vhdx\.zip)""");
                if (!match.Success)
                    return null;

                var serial = await TryParseSerialAsync(client, dirUri + "unpacked/build-info.txt", cancellationToken).ConfigureAwait(false);
                return new ChannelImage(dirUri + match.Groups[1].Value, dirUri + "SHA256SUMS", serial);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No Ubuntu {Series} image under {DirUri}", series, dirUri);
                return null;
            }
        }

        private static GalleryItem BuildVhdxItem(SeriesInfo series, ChannelImage? ga, ChannelImage? daily)
        {
            // The GA build is the card's own release; a series still on the
            // daily channel only (no GA published yet, e.g. a fresh LTS) is
            // described by its daily build until one appears.
            var primary = ga ?? daily!;

            return new GalleryItem
            {
                Name = $"Ubuntu {series.Version}",
                Publisher = PublisherName,
                Description = BaseDescription,
                ThumbnailUri = ThumbnailUri,
                DiskUri = primary.DiskUri,
                ChecksumUri = primary.ChecksumUri,
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = series.Version,
                LastUpdated = FormatSerial(primary.Serial) ?? series.Release.ToLongDateString(),
                LinuxDistro = LinuxDistro.Ubuntu,
                ReleaseVariants = (ga != null && daily != null)
                    ? new List<ReleaseVariant> { BuildDailyVariant(daily) }
                    : null
            };
        }

        private static ReleaseVariant BuildDailyVariant(ChannelImage daily) => new()
        {
            Id = "daily",
            DisplayName = daily.Serial is null ? "Daily build" : $"Daily build {daily.Serial}",
            Description = "Rebuilt daily from the current release with the latest package updates.",
            DiskUri = daily.DiskUri,
            ChecksumUri = daily.ChecksumUri,
            Version = daily.Serial ?? "daily",
            LastUpdated = FormatSerial(daily.Serial)
        };

        /// <summary>
        /// Desktop ISO card for a supported series the mirror carries no
        /// Hyper-V image for (interim releases never get one). Manual
        /// install, same as the Debian/Mint ISO cards.
        /// </summary>
        private async Task<GalleryItem?> TryLoadIsoItemAsync(HttpClient client, SeriesInfo series, CancellationToken cancellationToken)
        {
            try
            {
                var dirUri = ReleasesBaseUri + series.Version.Split(' ')[0] + "/";
                var html = await FetchAsync(client, dirUri, cancellationToken).ConfigureAwait(false);
                var match = Regex.Match(html, @"href=""(ubuntu-[\d.]+-desktop-amd64\.iso)""");
                if (!match.Success)
                    return null;

                return new GalleryItem
                {
                    Name = $"Ubuntu {series.Version}",
                    Publisher = PublisherName,
                    Description = BaseDescription + " Desktop ISO for manual installation.",
                    ThumbnailUri = ThumbnailUri,
                    DiskUri = dirUri + match.Groups[1].Value,
                    ChecksumUri = dirUri + "SHA256SUMS",
                    SecureBoot = "false",
                    EnhancedSessionTransportType = "HvSocket",
                    Version = series.Version,
                    LastUpdated = series.Release.ToLongDateString(),
                    LinuxDistro = LinuxDistro.Ubuntu
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No Ubuntu {Series} desktop ISO under {ReleasesBaseUri}", series.Series, ReleasesBaseUri);
                return null;
            }
        }

        /// <summary>
        /// Parses distro-info-data's ubuntu.csv, keeping series that are
        /// released and not yet end-of-life, newest first.
        /// </summary>
        private static List<SeriesInfo> ParseSupportedSeries(string csv)
        {
            var today = DateTime.UtcNow.Date;
            var series = new List<SeriesInfo>();

            foreach (var rawLine in csv.Split('\n').Skip(1))
            {
                var cols = rawLine.Trim().Split(',');
                if (cols.Length < 6)
                    continue;

                var versionMatch = Regex.Match(cols[0], @"^(\d+\.\d+)");
                if (!versionMatch.Success
                    || !double.TryParse(versionMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || !DateTime.TryParseExact(cols[4].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var release)
                    || !DateTime.TryParseExact(cols[5].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var eol))
                    continue;

                // Unreleased (future) and end-of-life series alike stay off the gallery.
                if (release > today || eol < today)
                    continue;

                series.Add(new SeriesInfo(cols[2].Trim(), cols[0].Trim(), number, release, eol));
            }

            return series.OrderByDescending(s => s.Number).ToList();
        }

        private static async Task<string> FetchAsync(HttpClient client, string uri, CancellationToken cancellationToken)
        {
            var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Reads "SERIAL=YYYYMMDD" from a channel's build-info.txt; null on any failure.</summary>
        private async Task<string?> TryParseSerialAsync(HttpClient client, string uri, CancellationToken cancellationToken)
        {
            try
            {
                var content = await FetchAsync(client, uri, cancellationToken).ConfigureAwait(false);
                var parts = content.Split('=');
                return parts.Length > 1 ? parts[1].Trim() : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static string? FormatSerial(string? serial) =>
            serial is not null
            && DateTime.TryParseExact(serial, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.ToLongDateString()
                : null;

        /// <summary>Pinned known-good GA pair used when live discovery fails.</summary>
        private static List<GalleryItem> PinnedFallbackItems() => new()
        {
            new GalleryItem
            {
                Name = "Ubuntu 22.04 LTS",
                Publisher = PublisherName,
                Description = BaseDescription,
                ThumbnailUri = ThumbnailUri,
                DiskUri = PinnedJammyUri,
                ChecksumUri = DesktopBaseUri + "jammy/release/current/SHA256SUMS",
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = "22.04 LTS",
                LastUpdated = FormatSerial("20220505"),
                LinuxDistro = LinuxDistro.Ubuntu
            },
            new GalleryItem
            {
                Name = "Ubuntu 24.04 LTS",
                Publisher = PublisherName,
                Description = BaseDescription,
                ThumbnailUri = ThumbnailUri,
                DiskUri = PinnedNobleUri,
                ChecksumUri = DesktopBaseUri + "noble/release/current/SHA256SUMS",
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = "24.04 LTS",
                LastUpdated = FormatSerial("20240706"),
                LinuxDistro = LinuxDistro.Ubuntu
            }
        };
    }
}
