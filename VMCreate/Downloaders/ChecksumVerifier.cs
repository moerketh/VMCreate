using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    public interface IChecksumVerifier
    {
        /// <summary>
        /// Downloads the checksum file, computes the hash of the local file,
        /// and throws if the values do not match.
        /// </summary>
        Task VerifyAsync(string filePath, string checksumUri, string algorithm,
                         CancellationToken cancellationToken,
                         IProgress<CreateVMProgressInfo> progress);

        /// <summary>
        /// Computes the hash of the local file and verifies it matches the
        /// expected hash. Throws if the values do not match.
        /// </summary>
        Task VerifyInlineAsync(string filePath, string expectedHash, string algorithm,
                               CancellationToken cancellationToken,
                               IProgress<CreateVMProgressInfo> progress);
    }

    public class ChecksumVerifier : IChecksumVerifier
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly ILogger<ChecksumVerifier> _logger;

        public ChecksumVerifier(IHttpClientFactory clientFactory, ILogger<ChecksumVerifier> logger)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task VerifyAsync(string filePath, string checksumUri, string algorithm,
                                      CancellationToken cancellationToken,
                                      IProgress<CreateVMProgressInfo> progress)
        {
            algorithm ??= "sha256";

            progress?.Report(new CreateVMProgressInfo
            {
                Phase = "Checksum",
                ProgressPercentage = 0
            });

            _logger.LogInformation("Downloading checksum file from {ChecksumUri}", checksumUri);

            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "VMCreate");

            var response = await client.GetAsync(checksumUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            var checksumContent = await response.Content.ReadAsStringAsync(cancellationToken);

            var fileName = Path.GetFileName(filePath);
            var expectedHash = ParseChecksum(checksumContent, fileName);

            if (string.IsNullOrEmpty(expectedHash))
                throw new InvalidOperationException(
                    $"Could not find checksum for '{fileName}' in checksum file at {checksumUri}");

            _logger.LogInformation("Expected {Algorithm} hash for {FileName}: {Hash}",
                algorithm, fileName, expectedHash);

            progress?.Report(new CreateVMProgressInfo
            {
                Phase = "Checksum",
                ProgressPercentage = 10
            });

            var actualHash = await ComputeHashAsync(filePath, algorithm, cancellationToken);

            progress?.Report(new CreateVMProgressInfo
            {
                Phase = "Checksum",
                ProgressPercentage = 100
            });

            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Checksum mismatch for {FileName}: expected {Expected}, got {Actual}. Deleting corrupt file.",
                    fileName, expectedHash, actualHash);
                File.Delete(filePath);
                throw new InvalidOperationException(
                    $"Checksum verification failed for '{fileName}'.\n" +
                    $"Expected: {expectedHash}\n" +
                    $"Actual:   {actualHash}\n" +
                    $"The downloaded file has been deleted. Please try again.");
            }

            _logger.LogInformation("Checksum verification passed for {FileName} ({Algorithm}: {Hash})",
                fileName, algorithm, actualHash);
        }

        /// <summary>
        /// Parses a checksum from common file formats:
        /// <list type="bullet">
        ///   <item>GNU coreutils: <c>hash  filename</c> or <c>hash *filename</c></item>
        ///   <item>BSD-style: <c>SHA256 (filename) = hash</c></item>
        ///   <item>Bare hash: single non-empty line containing only a hex string</item>
        /// </list>
        /// </summary>
        internal static string ParseChecksum(string content, string fileName)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            string bareHash = null;
            int bareHashCount = 0;

            foreach (var rawLine in content.Split('\n'))
            {
                var trimmed = rawLine.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    continue;

                // Format: BSD-style  "SHA256 (filename) = hash"
                var bsdMatch = Regex.Match(trimmed, @"^\w+\s+\((.+?)\)\s*=\s*([0-9a-fA-F]+)$");
                if (bsdMatch.Success)
                {
                    if (string.Equals(bsdMatch.Groups[1].Value, fileName, StringComparison.OrdinalIgnoreCase))
                        return bsdMatch.Groups[2].Value;
                    continue;
                }

                // Format: GNU coreutils  "hash  filename" or "hash *filename"
                var gnuMatch = Regex.Match(trimmed, @"^([0-9a-fA-F]+)\s+\*?(.+)$");
                if (gnuMatch.Success)
                {
                    if (string.Equals(gnuMatch.Groups[2].Value.Trim(), fileName, StringComparison.OrdinalIgnoreCase))
                        return gnuMatch.Groups[1].Value;
                    continue;
                }

                // Format: bare hash (collect for fallback)
                if (Regex.IsMatch(trimmed, @"^[0-9a-fA-F]{32,128}$"))
                {
                    bareHash = trimmed;
                    bareHashCount++;
                }
            }

            // Only use bare hash if there was exactly one
            return bareHashCount == 1 ? bareHash : null;
        }

        public async Task VerifyInlineAsync(string filePath, string expectedHash, string algorithm,
                                              CancellationToken cancellationToken,
                                              IProgress<CreateVMProgressInfo> progress)
        {
            algorithm ??= "sha256";

            progress?.Report(new CreateVMProgressInfo
            {
                Phase = "Checksum",
                ProgressPercentage = 0
            });

            var fileName = Path.GetFileName(filePath);
            _logger.LogInformation("Verifying {Algorithm} checksum for {FileName}", algorithm, fileName);

            var actualHash = await ComputeHashAsync(filePath, algorithm, cancellationToken);

            progress?.Report(new CreateVMProgressInfo
            {
                Phase = "Checksum",
                ProgressPercentage = 100
            });

            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Checksum mismatch for {FileName}: expected {Expected}, got {Actual}. Deleting corrupt file.",
                    fileName, expectedHash, actualHash);
                File.Delete(filePath);
                throw new InvalidOperationException(
                    $"Checksum verification failed for '{fileName}'.\n" +
                    $"Expected: {expectedHash}\n" +
                    $"Actual:   {actualHash}\n" +
                    $"The downloaded file has been deleted. Please try again.");
            }

            _logger.LogInformation("Checksum verification passed for {FileName} ({Algorithm}: {Hash})",
                fileName, algorithm, actualHash);
        }

        private static async Task<string> ComputeHashAsync(string filePath, string algorithm,
                                                            CancellationToken cancellationToken)
        {
            using var hashAlgorithm = algorithm.ToLowerInvariant() switch
            {
                "sha256" => (HashAlgorithm)SHA256.Create(),
                "sha512" => SHA512.Create(),
                _ => throw new NotSupportedException($"Unsupported checksum algorithm: {algorithm}")
            };

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                              FileShare.Read, 81920, useAsync: true);
            var hash = await hashAlgorithm.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
