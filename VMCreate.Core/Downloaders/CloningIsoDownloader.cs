using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.Gallery;

namespace VMCreate
{
    public interface ICloningIsoDownloader
    {
        /// <summary>
        /// Ensures the cloning ISO at <see cref="VmDeploymentPlan.CloningIsoPath"/> is the latest
        /// hyperv-convert-iso release. Downloads from GitHub when the ISO is missing, when the
        /// version file written next to it is missing, or when the recorded version is outdated.
        /// Keeps the cached copy when GitHub cannot be reached and its version is already known.
        /// </summary>
        Task EnsureIsoAsync(VmDeploymentPlan plan, CancellationToken cancellationToken,
                            IProgress<CreateVMProgressInfo>? progress);
    }

    public class CloningIsoDownloader : ICloningIsoDownloader
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/moerketh/hyperv-convert-iso/releases/latest";
        // v2 channel: list releases (NOT /latest — GitHub skips prereleases
        // there, hiding every 2.0.0-alpha) and pick per the policy below.
        private const string GitHubReleasesListUrl = "https://api.github.com/repos/moerketh/vmcreate-iso/releases?per_page=20";
        private const int MaxRetries = 3;
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

        private readonly IDownloader _downloader;
        private readonly IChecksumVerifier _checksumVerifier;
        private readonly IHttpClientFactory _clientFactory;
        private readonly ILogger<CloningIsoDownloader> _logger;

        public CloningIsoDownloader(
            IDownloader downloader,
            IChecksumVerifier checksumVerifier,
            IHttpClientFactory clientFactory,
            ILogger<CloningIsoDownloader> logger)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _checksumVerifier = checksumVerifier ?? throw new ArgumentNullException(nameof(checksumVerifier));
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task EnsureIsoAsync(VmDeploymentPlan plan, CancellationToken cancellationToken,
                                         IProgress<CreateVMProgressInfo>? progress)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            cancellationToken.ThrowIfCancellationRequested();

            // Channel per flag: v2 caches at its own path with its own
            // sidecar, so toggling never re-downloads and the generations
            // never overwrite each other.
            bool v2 = plan.IsoVsockEnabled;
            string isoPath = plan.EffectiveCloningIsoPath;
            string versionPath = GetVersionFilePath(isoPath);
            bool isoExists = File.Exists(isoPath);
            string? cachedVersion = isoExists ? TryReadCachedVersion(versionPath) : null;

            string isoUrl;
            string? checksumUrl;
            string version;

            if (cachedVersion != null)
            {
                try
                {
                    (isoUrl, checksumUrl, version) = v2
                        ? await GetLatestV2ReleaseAsync(cancellationToken)
                        : await GetLatestReleaseAsync(cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex,
                        "Could not check for a newer cloning ISO release. Using cached ISO {Version} at {IsoPath}.",
                        cachedVersion, isoPath);
                    return;
                }

                if (string.Equals(cachedVersion, version, StringComparison.Ordinal))
                {
                    _logger.LogInformation("Cloning ISO {Version} already present at {IsoPath}", cachedVersion, isoPath);
                    return;
                }

                _logger.LogInformation(
                    "Cached cloning ISO at {IsoPath} is version {CachedVersion}, latest is {LatestVersion}. Downloading the updated release.",
                    isoPath, cachedVersion, version);
            }
            else
            {
                if (isoExists)
                    _logger.LogInformation(
                        "Cloning ISO found at {IsoPath} without a readable version file. Treating it as outdated and fetching the latest release.",
                        isoPath);
                else
                    _logger.LogInformation("Cloning ISO not found at {IsoPath}. Fetching latest release from GitHub.", isoPath);

                (isoUrl, checksumUrl, version) = v2
                    ? await GetLatestV2ReleaseAsync(cancellationToken)
                    : await GetLatestReleaseAsync(cancellationToken);
            }

            progress?.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.DownloadCloningIso));

            string tempPath = await _downloader.DownloadFileAsync(isoUrl, cancellationToken, progress, useCache: false);

            try
            {
                if (checksumUrl != null)
                {
                    string isoFileName = Path.GetFileName(new Uri(isoUrl).LocalPath);
                    await _checksumVerifier.VerifyAsync(tempPath, checksumUrl, "sha256",
                        cancellationToken, progress, expectedFileName: isoFileName);
                }

                string? directory = Path.GetDirectoryName(isoPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.Move(tempPath, isoPath, overwrite: true);
                await File.WriteAllTextAsync(versionPath, version, cancellationToken);
                _logger.LogInformation("Cloning ISO {Version} saved to {IsoPath}", version, isoPath);
            }
            catch
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                throw;
            }
        }

        private static string GetVersionFilePath(string isoPath)
            => isoPath + ".version";

        private static string? TryReadCachedVersion(string versionPath)
        {
            try
            {
                if (!File.Exists(versionPath))
                    return null;

                string? version = File.ReadAllText(versionPath).Trim();
                return string.IsNullOrEmpty(version) ? null : version;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private async Task<(string IsoUrl, string? ChecksumUrl, string Version)> GetLatestReleaseAsync(CancellationToken cancellationToken)
        {
            HttpRequestException? lastError = null;

            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var client = _clientFactory.CreateClient();
                    client.Timeout = HttpTimeout;
                    client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);

                    string json = await client.GetStringAsync(GitHubApiUrl, cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    var assets = doc.RootElement.GetProperty("assets").EnumerateArray().ToList();

                    string? isoUrl = null;
                    string? checksumUrl = null;

                    foreach (var asset in assets)
                    {
                        string? name = asset.GetProperty("name").GetString();
                        string? url = asset.GetProperty("browser_download_url").GetString();

                        if (name?.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) == true)
                            isoUrl = url;
                        else if (name?.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) == true)
                            checksumUrl = url;
                    }

                    if (isoUrl == null)
                        throw new InvalidOperationException(
                            "No .iso asset found in the latest hyperv-convert-iso release. " +
                            "Check https://github.com/moerketh/hyperv-convert-iso/releases");

                    string? tag = null;
                    if (!doc.RootElement.TryGetProperty("tag_name", out var tagElement) ||
                        string.IsNullOrEmpty(tag = tagElement.GetString()))
                        throw new InvalidOperationException(
                            "The latest hyperv-convert-iso release has no tag_name. " +
                            "Check https://github.com/moerketh/hyperv-convert-iso/releases");

                    string version = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;

                    _logger.LogInformation("Latest hyperv-convert-iso release {Version}: {IsoUrl}", version, isoUrl);
                    return (isoUrl, checksumUrl, version);
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex;
                    _logger.LogWarning(ex, "GitHub API attempt {Attempt} failed", attempt + 1);

                    if (attempt < MaxRetries - 1)
                        await Task.Delay(RetryDelays[attempt], cancellationToken);
                }
            }

            throw new Exception($"Failed to query the latest hyperv-convert-iso release after {MaxRetries} attempts: {lastError?.Message}", lastError);
        }

        /// <summary>
        /// v2 channel: lists vmcreate-iso releases and picks per policy —
        /// the NEWEST STABLE release wins; a prerelease is only picked when
        /// no stable release exists yet (the alpha period). Array order is
        /// never trusted: candidates sort by semver with published_at as
        /// the tiebreak.
        /// </summary>
        private async Task<(string IsoUrl, string? ChecksumUrl, string Version)> GetLatestV2ReleaseAsync(CancellationToken cancellationToken)
        {
            HttpRequestException? lastError = null;

            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var client = _clientFactory.CreateClient();
                    client.Timeout = HttpTimeout;
                    client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);

                    string json = await client.GetStringAsync(GitHubReleasesListUrl, cancellationToken);
                    using var doc = JsonDocument.Parse(json);

                    (string Tag, bool Prerelease, string PublishedAt, string? IsoUrl, string? ChecksumUrl) best =
                        default;
                    bool haveBest = false;
                    bool bestIsStable = false;

                    foreach (var release in doc.RootElement.EnumerateArray())
                    {
                        if (!release.TryGetProperty("tag_name", out var tagEl)) continue;
                        string? tag = tagEl.GetString();
                        if (string.IsNullOrEmpty(tag)) continue;

                        bool prerelease = release.TryGetProperty("prerelease", out var preEl)
                                          && preEl.ValueKind == JsonValueKind.True;

                        string published = release.TryGetProperty("published_at", out var pubEl)
                            ? pubEl.GetString() ?? "" : "";

                        string? isoUrl = null, checksumUrl = null;
                        if (release.TryGetProperty("assets", out var assetsEl))
                        {
                            foreach (var asset in assetsEl.EnumerateArray())
                            {
                                string? name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                                string? url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                                if (name == null || url == null) continue;
                                if (name.StartsWith("vmcreate-", StringComparison.OrdinalIgnoreCase)
                                    && name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                                    isoUrl = url;
                                else if (name.StartsWith("vmcreate-", StringComparison.OrdinalIgnoreCase)
                                         && name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                                    checksumUrl = url;
                            }
                        }
                        if (isoUrl == null) continue; // no matching asset (e.g. draft)

                        // Policy: stable beats prerelease at equal-or-any rank;
                        // within the same class, newest semver (published_at
                        // tiebreak) wins.
                        bool candidateIsStable = !prerelease;
                        int cmp = haveBest
                            ? CompareCandidates(tag, published, best.Tag, best.PublishedAt)
                            : 1;
                        bool take;
                        if (!haveBest) take = true;
                        else if (candidateIsStable != bestIsStable) take = candidateIsStable;
                        else take = cmp > 0;

                        if (take)
                        {
                            best = (tag, prerelease, published, isoUrl, checksumUrl);
                            haveBest = true;
                            bestIsStable = candidateIsStable;
                        }
                    }

                    if (!haveBest || best.IsoUrl == null)
                        throw new InvalidOperationException(
                            "No vmcreate-*.iso asset found in any vmcreate-iso release. " +
                            "Check https://github.com/moerketh/vmcreate-iso/releases");

                    string version = best.Tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? best.Tag[1..] : best.Tag;
                    _logger.LogInformation("Selected vmcreate-iso release {Version} (stable={Stable}): {IsoUrl}",
                        version, bestIsStable, best.IsoUrl);
                    return (best.IsoUrl, best.ChecksumUrl, version);
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex;
                    _logger.LogWarning(ex, "GitHub API attempt {Attempt} failed (v2 releases list)", attempt + 1);

                    if (attempt < MaxRetries - 1)
                        await Task.Delay(RetryDelays[attempt], cancellationToken);
                }
            }

            throw new Exception($"Failed to query vmcreate-iso releases after {MaxRetries} attempts: {lastError?.Message}", lastError);
        }

        /// <summary>
        /// Orders two release tags: semver-aware on the numeric core
        /// (v2.10.0 &gt; v2.9.1), prerelease-suffix aware within the same
        /// core (2.0.0 &lt; 2.0.0-alpha.1's successor ordering per semver:
        /// a prerelease sorts BELOW its own release), published_at as the
        /// fallback for non-semver garbage. Positive = a is newer.
        /// </summary>
        internal static int CompareCandidates(string tagA, string publishedA, string tagB, string publishedB)
        {
            int coreA = SemverCoreRank(tagA, out string? suffixA);
            int coreB = SemverCoreRank(tagB, out string? suffixB);
            if (coreA != coreB) return coreA.CompareTo(coreB);

            // Same core: a plain release outranks any of its prereleases.
            bool preA = suffixA != null, preB = suffixB != null;
            if (preA != preB) return preB ? 1 : -1;
            if (!preA) return 0; // identical stable tags

            // Both prereleases on the same core: compare suffixes
            // lexically-by-dot-segment (alpha < beta < rc), then fall back
            // to published time.
            int cmp = StringComparer.OrdinalIgnoreCase.Compare(suffixA, suffixB);
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(publishedA, publishedB);
        }

        /// <summary>Encodes a semver tag's numeric core as a comparable
        /// rank (major*1_000_000 + minor*1_000 + patch); -1 when the tag
        /// does not parse (sorting then falls back to publish time via the
        /// caller path where ranks tie at -1... only if BOTH fail).</summary>
        private static int SemverCoreRank(string tag, out string? suffix)
        {
            suffix = null;
            string t = tag.Trim();
            if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t[1..];
            int dash = t.IndexOf('-');
            if (dash >= 0)
            {
                suffix = t[(dash + 1)..];
                t = t[..dash];
            }
            string[] parts = t.Split('.');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out int major)
                || !int.TryParse(parts[1], out int minor)
                || !int.TryParse(parts[2], out int patch))
                return -1;
            return major * 1_000_000 + minor * 1_000 + patch;
        }
    }
}
