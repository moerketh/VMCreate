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

            string isoPath = plan.CloningIsoPath;
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
                    (isoUrl, checksumUrl, version) = await GetLatestReleaseAsync(cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex,
                        "Could not check for a newer hyperv-convert-iso release. Using cached ISO {Version} at {IsoPath}.",
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

                (isoUrl, checksumUrl, version) = await GetLatestReleaseAsync(cancellationToken);
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
    }
}
