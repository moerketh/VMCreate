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
        /// Ensures the cloning ISO is present at <see cref="VmSettings.CloningIsoPath"/>.
        /// Downloads the latest release from GitHub if the file is missing.
        /// </summary>
        Task EnsureIsoAsync(VmSettings vmSettings, CancellationToken cancellationToken,
                            IProgress<CreateVMProgressInfo> progress);
    }

    public class CloningIsoDownloader : ICloningIsoDownloader
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/moerketh/hyperv-convert-iso/releases/latest";
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

        public async Task EnsureIsoAsync(VmSettings vmSettings, CancellationToken cancellationToken,
                                         IProgress<CreateVMProgressInfo> progress)
        {
            string isoPath = vmSettings.CloningIsoPath;

            if (File.Exists(isoPath))
            {
                _logger.LogInformation("Cloning ISO already present at {IsoPath}", isoPath);
                return;
            }

            _logger.LogInformation("Cloning ISO not found at {IsoPath}. Fetching latest release from GitHub.", isoPath);

            var (isoUrl, checksumUrl) = await GetLatestReleaseUrlsAsync(cancellationToken);

            progress?.Report(new CreateVMProgressInfo { Phase = "DownloadCloningIso" });

            string tempPath = await _downloader.DownloadFileAsync(isoUrl, cancellationToken, progress, useCache: false);

            try
            {
                if (checksumUrl != null)
                {
                    await _checksumVerifier.VerifyAsync(tempPath, checksumUrl, "sha256", cancellationToken, progress);
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
            var client = _clientFactory.CreateClient();
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
    }
}
