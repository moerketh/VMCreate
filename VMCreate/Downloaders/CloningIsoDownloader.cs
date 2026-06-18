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
        /// Ensures the cloning ISO is present at <see cref="VmDeploymentPlan.CloningIsoPath"/>.
        /// Downloads the latest release from GitHub if the file is missing.
        /// </summary>
        Task EnsureIsoAsync(VmDeploymentPlan plan, CancellationToken cancellationToken,
                            IProgress<CreateVMProgressInfo> progress);
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
                                         IProgress<CreateVMProgressInfo> progress)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            cancellationToken.ThrowIfCancellationRequested();

            string isoPath = plan.CloningIsoPath;

            if (File.Exists(isoPath))
            {
                _logger.LogInformation("Cloning ISO already present at {IsoPath}", isoPath);
                return;
            }

            _logger.LogInformation("Cloning ISO not found at {IsoPath}. Fetching latest release from GitHub.", isoPath);

            var (isoUrl, checksumUrl) = await GetLatestReleaseUrlsAsync(cancellationToken);

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

                string directory = Path.GetDirectoryName(isoPath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.Move(tempPath, isoPath, overwrite: true);
                _logger.LogInformation("Cloning ISO saved to {IsoPath}", isoPath);
            }
            catch
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                throw;
            }
        }

        private async Task<(string IsoUrl, string ChecksumUrl)> GetLatestReleaseUrlsAsync(CancellationToken cancellationToken)
        {
            HttpRequestException lastError = null;

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

                    string isoUrl = null;
                    string checksumUrl = null;

                    foreach (var asset in assets)
                    {
                        string name = asset.GetProperty("name").GetString();
                        string url = asset.GetProperty("browser_download_url").GetString();

                        if (name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                            isoUrl = url;
                        else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                            checksumUrl = url;
                    }

                    if (isoUrl == null)
                        throw new InvalidOperationException(
                            "No .iso asset found in the latest hyperv-convert-iso release. " +
                            "Check https://github.com/moerketh/hyperv-convert-iso/releases");

                    _logger.LogInformation("Latest hyperv-convert-iso release: {IsoUrl}", isoUrl);
                    return (isoUrl, checksumUrl);
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
