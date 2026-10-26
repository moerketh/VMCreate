using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.Gallery;

namespace VMCreate
{
    public interface ICloningIsoDownloader
    {
        /// <summary>
        /// Ensures the cloning ISO at <see cref="VmDeploymentPlan.CloningIsoPath"/> is a
        /// verified vmcreate-iso release. A release counts only when it publishes the
        /// ISO plus <c>vmcreate-&lt;ver&gt;.manifest.json</c> and its <c>.sig</c>; the
        /// manifest signature must verify against a key in <see cref="IsoReleaseTrust"/>
        /// and the ISO's size and SHA-256 must match it before anything reaches the
        /// cache. Keeps the cached copy when GitHub cannot be reached and the cache
        /// still verifies.
        /// </summary>
        Task EnsureIsoAsync(VmDeploymentPlan plan, CancellationToken cancellationToken,
                            IProgress<CreateVMProgressInfo>? progress);

        /// <summary>
        /// Re-checks the cached ISO against its cached manifest right before the VM
        /// boots from it, and returns a disposable guard that holds a
        /// <c>FileShare.Read</c> handle on the verified ISO — nothing can replace
        /// the verified bytes while the boot cycle runs. Throws when the cache is
        /// missing, unsigned, or no longer matches — offline included; an
        /// unverified cache never boots.
        /// </summary>
        Task<IIsoReadGuard> VerifyCachedIsoGuardedAsync(VmDeploymentPlan plan, CancellationToken cancellationToken);
    }

    /// <summary>
    /// A held <c>FileShare.Read</c> handle on the verified cloning ISO. Dispose
    /// when the ISO boot cycle ends — the file cannot be replaced while the
    /// guard lives (Hyper-V opens the DVD with read sharing).
    /// </summary>
    public interface IIsoReadGuard : IDisposable
    {
    }

    /// <summary>
    /// A verified vmcreate-iso release manifest and the bytes it was verified over.
    /// <see cref="ManifestBytes"/> is the exact downloaded content — the signature
    /// covers it and the cache stores it byte-for-byte.
    /// </summary>
    public sealed record VerifiedIsoManifest
    {
        public required byte[] ManifestBytes { get; init; }
        public required byte[] SignatureBytes { get; init; }
        public required string Version { get; init; }
        public required string Tag { get; init; }
        public required string Name { get; init; }
        public required long Schema { get; init; }
        public required string IsoFile { get; init; }
        public required long IsoSize { get; init; }
        public required string IsoSha256 { get; init; }
        public required string KeyId { get; init; }
    }

    public class CloningIsoDownloader : ICloningIsoDownloader
    {
        // List releases (NOT /latest — GitHub skips prereleases there,
        // hiding every 2.0.0-alpha) and pick per the selection policy.
        private const string GitHubReleasesListUrl = "https://api.github.com/repos/moerketh/vmcreate-iso/releases?per_page=20";
        private const int MaxRetries = 3;
        // The manifest and its signature are tiny (a few hundred bytes);
        // a response larger than this is not one of ours.
        private const int MaxManifestBytes = 1024;
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

        private readonly IDownloader _downloader;
        private readonly IHttpClientFactory _clientFactory;
        private readonly ILogger<CloningIsoDownloader> _logger;
        private readonly IsoReleaseTrust _trust;

        public CloningIsoDownloader(
            IDownloader downloader,
            IHttpClientFactory clientFactory,
            ILogger<CloningIsoDownloader> logger)
            : this(downloader, clientFactory, logger, IsoReleaseTrust.BuiltIn)
        {
        }

        /// <summary>
        /// Test seam: verify against a test key instead of the built-in
        /// trust store, so no test key ever ships. Internal — production
        /// code always uses <see cref="IsoReleaseTrust.BuiltIn"/>.
        /// </summary>
        internal CloningIsoDownloader(
            IDownloader downloader,
            IHttpClientFactory clientFactory,
            ILogger<CloningIsoDownloader> logger,
            IsoReleaseTrust trust)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        }

        public async Task EnsureIsoAsync(VmDeploymentPlan plan, CancellationToken cancellationToken,
                                         IProgress<CreateVMProgressInfo>? progress)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            cancellationToken.ThrowIfCancellationRequested();

            // Cache layout: the ISO (CloningIsoPath) plus the verified
            // release manifest and its signature stored next to it.
            string isoPath = plan.CloningIsoPath;
            VerifiedIsoManifest? cached = TryVerifyCachedManifest(isoPath, requireIso: true);
            if (cached != null && !await CachedIsoMatchesManifestAsync(isoPath, cached, cancellationToken))
            {
                // A signed manifest whose ISO no longer matches means the
                // ISO was corrupted or replaced after verification. Treat
                // it as no cache: re-download the release so a damaged
                // cache repairs itself instead of failing every deploy
                // at the pre-boot check ("delete it by hand").
                _logger.LogWarning(
                    "The cached cloning ISO at {IsoPath} does not match its signed manifest (size or SHA-256 changed). " +
                    "Re-downloading the release to repair the cache.",
                    isoPath);
                cached = null;
            }

            SignedRelease? latest;
            try
            {
                latest = await GetLatestSignedReleaseAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (cached != null)
                {
                    _logger.LogWarning(ex,
                        "Could not check for a newer cloning ISO release. Using verified cached ISO {Version} at {IsoPath}.",
                        cached.Version, isoPath);
                    return;
                }

                if (File.Exists(isoPath))
                    throw new InvalidOperationException(
                        "Could not reach GitHub to verify the cached cloning ISO, and the cache has no valid " +
                        "signed manifest. The ISO cannot be trusted for boot. Delete " + isoPath +
                        " and retry while online.", ex);

                throw;
            }

            // Point 6: a release OLDER than the verified cache is refused —
            // the newest verified ISO already on disk wins. This covers a
            // newer release being deleted on GitHub (the list then falls
            // back to an older one).
            if (cached != null
                && CompareCandidates(latest.Tag, "", cached.Tag, "") < 0)
            {
                _logger.LogWarning(
                    "The newest signed vmcreate-iso release is {LatestVersion}, older than the verified cached {CachedVersion}. Keeping the cached ISO.",
                    latest.Version, cached.Version);
                return;
            }

            if (cached != null && string.Equals(cached.Version, latest.Version, StringComparison.Ordinal))
            {
                _logger.LogInformation("Cloning ISO {Version} already present at {IsoPath}", cached.Version, isoPath);
                return;
            }

            if (cached != null)
                _logger.LogInformation(
                    "Cached cloning ISO at {IsoPath} is version {CachedVersion}, latest signed release is {LatestVersion}. Downloading the updated release.",
                    isoPath, cached.Version, latest.Version);
            else if (File.Exists(isoPath))
                _logger.LogInformation(
                    "Cloning ISO found at {IsoPath} without a valid signed manifest. Treating it as outdated and fetching the latest signed release.",
                    isoPath);
            else
                _logger.LogInformation("Cloning ISO not found at {IsoPath}. Fetching latest signed release from GitHub.", isoPath);

            progress?.Report(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.DownloadCloningIso));

            // 1. Manifest and signature first (both tiny): the signature is
            //    checked BEFORE any field of the manifest is trusted.
            byte[] manifestBytes = await DownloadSmallFileAsync(latest.ManifestUrl, cancellationToken);
            byte[] signatureBytes = await DownloadSmallFileAsync(latest.SignatureUrl, cancellationToken);

            if (_trust.VerifyManifestSignature(manifestBytes, signatureBytes) == null)
                throw new InvalidOperationException(
                    $"The vmcreate-iso release {latest.Version} publishes a manifest whose signature does not " +
                    "verify against any key built into VMCreate. Refusing to install it.");

            // 2. Only now are the manifest's fields read and bound to the
            //    release it was downloaded from.
            VerifiedIsoManifest manifest = ParseVerifiedManifest(manifestBytes, signatureBytes);
            if (manifest.Schema != 1 || manifest.Name != "vmcreate-iso")
                throw new InvalidOperationException(
                    $"The vmcreate-iso release {latest.Version} publishes an unsupported manifest " +
                    $"(schema '{manifest.Schema}', name '{manifest.Name}'). Refusing to install it.");
            if (!string.Equals(manifest.Version, latest.Version, StringComparison.Ordinal)
                || !string.Equals(manifest.Tag, latest.Tag, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The vmcreate-iso release {latest.Version} publishes a manifest for version " +
                    $"'{manifest.Version}' (tag '{manifest.Tag}') that does not match the release. Refusing to install it.");
            string isoFileName = Path.GetFileName(new Uri(latest.IsoUrl).LocalPath);
            if (!string.Equals(manifest.IsoFile, isoFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The vmcreate-iso release {latest.Version} publishes a manifest naming ISO file " +
                    $"'{manifest.IsoFile}' but the release ISO is '{isoFileName}'. Refusing to install it.");

            // 3. The ISO itself — size and SHA-256 must match the signed
            //    manifest before it goes into the cache.
            string tempPath = await _downloader.DownloadFileAsync(latest.IsoUrl, cancellationToken, progress, useCache: false);

            try
            {
                await VerifyIsoFileAsync(tempPath, manifest, cancellationToken);

                string? directory = Path.GetDirectoryName(isoPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.Move(tempPath, isoPath, overwrite: true);
                await File.WriteAllBytesAsync(GetManifestPath(isoPath), manifest.ManifestBytes, cancellationToken);
                await File.WriteAllBytesAsync(GetSignaturePath(isoPath), manifest.SignatureBytes, cancellationToken);
                TryDeleteLegacyVersionFile(isoPath);
                _logger.LogInformation("Cloning ISO {Version} (key {KeyId}) saved to {IsoPath}",
                    manifest.Version, manifest.KeyId, isoPath);
            }
            catch
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                throw;
            }
        }

        public async Task<IIsoReadGuard> VerifyCachedIsoGuardedAsync(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            cancellationToken.ThrowIfCancellationRequested();

            string isoPath = plan.CloningIsoPath;
            VerifiedIsoManifest? manifest = TryVerifyCachedManifest(isoPath, requireIso: true);
            if (manifest == null)
                throw new InvalidOperationException(
                    $"The cached cloning ISO at {isoPath} has no valid signed manifest — it cannot be trusted for boot. " +
                    "Delete it and deploy again while online.");

            // Open the lock FIRST and verify through THAT handle: opening
            // a second handle after hashing would leave a window in which
            // the file could be swapped between check and lock. Holding
            // FileShare.Read blocks any writer, and Hyper-V opens the DVD
            // with read sharing so the boot still works.
            FileStream stream;
            try
            {
                stream = File.Open(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Could not open the cached cloning ISO at {isoPath} for the pre-boot check. " +
                    "It may be in use by another process.", ex);
            }

            try
            {
                if (stream.Length != manifest.IsoSize)
                    throw new InvalidOperationException(
                        $"The cloning ISO '{Path.GetFileName(isoPath)}' has size {stream.Length:N0} bytes, but its signed " +
                        $"manifest records {manifest.IsoSize:N0} bytes. The file has been changed since verification — " +
                        "refusing to boot it.");

                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
                string actualSha = Convert.ToHexString(hash).ToLowerInvariant();
                if (!string.Equals(actualSha, manifest.IsoSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"The cloning ISO '{Path.GetFileName(isoPath)}' has SHA-256 {actualSha}, but its signed manifest " +
                        $"records {manifest.IsoSha256}. The file has been changed since verification — refusing to boot it.");

                _logger.LogInformation("Pre-boot check passed for cloning ISO {Version} at {IsoPath}",
                    manifest.Version, isoPath);
                return new IsoReadGuard(stream);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }

        private sealed class IsoReadGuard : IIsoReadGuard
        {
            private FileStream? _stream;

            public IsoReadGuard(FileStream stream) => _stream = stream;

            public void Dispose()
            {
                _stream?.Dispose();
                _stream = null;
            }
        }

        /// <summary>
        /// Verifies a downloaded ISO's size and SHA-256 against its already
        /// signature-verified manifest.
        /// </summary>
        private async Task VerifyIsoFileAsync(string isoPath, VerifiedIsoManifest manifest,
                                               CancellationToken cancellationToken)
        {
            var file = new FileInfo(isoPath);
            if (!file.Exists)
                throw new InvalidOperationException(
                    $"The cloning ISO is missing at {isoPath}.");

            if (file.Length != manifest.IsoSize)
                throw new InvalidOperationException(
                    $"The cloning ISO '{Path.GetFileName(isoPath)}' has size {file.Length:N0} bytes, but its signed " +
                    $"manifest records {manifest.IsoSize:N0} bytes. The file has been changed since verification — " +
                    "refusing to boot it.");

            string actualSha = await ComputeSha256Async(isoPath, cancellationToken);
            if (!string.Equals(actualSha, manifest.IsoSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The cloning ISO '{Path.GetFileName(isoPath)}' has SHA-256 {actualSha}, but its signed manifest " +
                    $"records {manifest.IsoSha256}. The file has been changed since verification — refusing to boot it.");
        }

        /// <summary>
        /// Whether the cached ISO still matches its signed manifest's size
        /// and SHA-256. False means the cache is damaged and must be
        /// re-downloaded. Read errors count as a mismatch: an unreadable
        /// cache is not usable.
        /// </summary>
        private async Task<bool> CachedIsoMatchesManifestAsync(string isoPath, VerifiedIsoManifest manifest,
                                                                CancellationToken cancellationToken)
        {
            try
            {
                await VerifyIsoFileAsync(isoPath, manifest, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The cached cloning ISO at {IsoPath} does not verify against its manifest.", isoPath);
                return false;
            }
        }

        /// <summary>
        /// Reads and signature-verifies the cached manifest next to the ISO.
        /// Returns null when any part of the cache is missing or fails
        /// verification; callers decide what that means (redownload vs.
        /// fail the deploy).
        /// </summary>
        private VerifiedIsoManifest? TryVerifyCachedManifest(string isoPath, bool requireIso)
        {
            try
            {
                string manifestPath = GetManifestPath(isoPath);
                string signaturePath = GetSignaturePath(isoPath);
                if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
                    return null;
                if (requireIso && !File.Exists(isoPath))
                    return null;

                byte[] manifestBytes = File.ReadAllBytes(manifestPath);
                byte[] signatureBytes = File.ReadAllBytes(signaturePath);

                if (_trust.VerifyManifestSignature(manifestBytes, signatureBytes) == null)
                {
                    _logger.LogWarning(
                        "The cached manifest at {ManifestPath} does not verify against any built-in key — the cache is untrusted.",
                        manifestPath);
                    return null;
                }

                return ParseVerifiedManifest(manifestBytes, signatureBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                      or FormatException or CryptographicException)
            {
                _logger.LogWarning(ex, "Could not read the cached manifest for {IsoPath}", isoPath);
                return null;
            }
        }

        internal static string GetManifestPath(string isoPath)
            => isoPath + ".manifest.json";

        internal static string GetSignaturePath(string isoPath)
            => isoPath + ".manifest.json.sig";

        /// <summary>
        /// Parses a manifest whose signature already verified. Field
        /// validation beyond schema/name/version/tag/iso_file happens in
        /// <see cref="EnsureIsoAsync"/>, where the release is known.
        /// </summary>
        private static VerifiedIsoManifest ParseVerifiedManifest(byte[] manifestBytes, byte[] signatureBytes)
        {
            using JsonDocument doc = JsonDocument.Parse(manifestBytes);
            JsonElement root = doc.RootElement;

            long ReadLong(string name)
            {
                if (root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number)
                    return el.GetInt64();
                return -1;
            }

            return new VerifiedIsoManifest
            {
                ManifestBytes = manifestBytes,
                SignatureBytes = signatureBytes,
                Version = root.TryGetProperty("version", out JsonElement v) ? v.GetString() ?? "" : "",
                Tag = root.TryGetProperty("tag", out JsonElement t) ? t.GetString() ?? "" : "",
                IsoFile = root.TryGetProperty("iso_file", out JsonElement f) ? f.GetString() ?? "" : "",
                IsoSize = ReadLong("iso_size"),
                IsoSha256 = root.TryGetProperty("iso_sha256", out JsonElement s) ? s.GetString() ?? "" : "",
                KeyId = root.TryGetProperty("key_id", out JsonElement k) ? k.GetString() ?? "" : "",
                Schema = ReadLong("schema"),
                Name = root.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
            };
        }

        private static void TryDeleteLegacyVersionFile(string isoPath)
        {
            try
            {
                string legacy = isoPath + ".version";
                if (File.Exists(legacy))
                    File.Delete(legacy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover .version file is inert — the manifest is the
                // only source of truth now.
            }
        }

        private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Downloads the manifest or signature (both under 1 KB). The size
        /// cap bounds what an attacker controlling the network can make
        /// VMCreate read from one URL.
        /// </summary>
        private async Task<byte[]> DownloadSmallFileAsync(string url, CancellationToken cancellationToken)
        {
            using var client = _clientFactory.CreateClient();
            client.Timeout = HttpTimeout;
            client.DefaultRequestHeaders.Add("User-Agent", ProductInfo.UserAgent);

            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            long? length = response.Content.Headers.ContentLength;
            if (length > MaxManifestBytes)
                throw new InvalidOperationException(
                    $"The file at {url} is {length} bytes — far larger than a release manifest. Refusing it.");

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaxManifestBytes)
                throw new InvalidOperationException(
                    $"The file at {url} exceeds {MaxManifestBytes} bytes — far larger than a release manifest. Refusing it.");
            return bytes;
        }

        /// <summary>
        /// v2 channel: lists vmcreate-iso releases and picks per policy —
        /// the NEWEST STABLE SIGNED release wins; a prerelease is only
        /// picked when no stable signed release exists yet. A release only
        /// counts when it publishes the ISO plus
        /// vmcreate-&lt;ver&gt;.manifest.json and its .sig — unsigned
        /// releases are skipped entirely (v2.0.0 and its .sha256-only
        /// siblings never qualify). Array order is never trusted:
        /// candidates sort by semver with published_at as the tiebreak.
        /// The repo is public: no authentication is needed for the
        /// releases API or the asset downloads.
        /// </summary>
        private async Task<SignedRelease> GetLatestSignedReleaseAsync(CancellationToken cancellationToken)
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

                    (string Tag, bool Prerelease, string PublishedAt, string IsoUrl, string ManifestUrl, string SignatureUrl)? best = null;
                    bool haveBest = false;
                    bool bestIsStable = false;
                    int signedCount = 0;

                    foreach (var release in doc.RootElement.EnumerateArray())
                    {
                        if (!release.TryGetProperty("tag_name", out var tagEl)) continue;
                        string? tag = tagEl.GetString();
                        if (string.IsNullOrEmpty(tag)) continue;

                        bool prerelease = release.TryGetProperty("prerelease", out var preEl)
                                          && preEl.ValueKind == JsonValueKind.True;

                        string published = release.TryGetProperty("published_at", out var pubEl)
                            ? pubEl.GetString() ?? "" : "";

                        // The release's tag names its exact assets. Matching
                        // the full file name (not just any *.manifest.json)
                        // leaves no room for an extra asset to shadow the
                        // real one.
                        string version = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                            ? tag[1..]
                            : tag;
                        string manifestAsset = $"vmcreate-{version}.manifest.json";

                        string? isoUrl = null, manifestUrl = null, signatureUrl = null;
                        if (release.TryGetProperty("assets", out var assetsEl))
                        {
                            foreach (var asset in assetsEl.EnumerateArray())
                            {
                                string? name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                                // Public repo: browser_download_url is the
                                // plain, unauthenticated download path.
                                string? url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                                if (name == null || url == null) continue;
                                if (name.StartsWith("vmcreate-", StringComparison.OrdinalIgnoreCase)
                                    && name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                                {
                                    isoUrl = url;
                                }
                                else if (name.Equals(manifestAsset, StringComparison.OrdinalIgnoreCase))
                                    manifestUrl = url;
                                else if (name.Equals(manifestAsset + ".sig", StringComparison.OrdinalIgnoreCase))
                                    signatureUrl = url;
                            }
                        }
                        // A release without ALL THREE assets is not a
                        // signed release: it cannot be verified before
                        // boot, so it never qualifies.
                        if (isoUrl == null || manifestUrl == null || signatureUrl == null) continue;
                        signedCount++;

                        // Policy: stable beats prerelease at equal-or-any rank;
                        // within the same class, newest semver (published_at
                        // tiebreak) wins.
                        bool candidateIsStable = !prerelease;
                        bool take;
                        if (!haveBest)
                            take = true;
                        else if (candidateIsStable != bestIsStable)
                            take = candidateIsStable;
                        else
                            take = CompareCandidates(tag, published, best!.Value.Tag, best.Value.PublishedAt) > 0;

                        if (take)
                        {
                            best = (tag, prerelease, published, isoUrl, manifestUrl, signatureUrl);
                            haveBest = true;
                            bestIsStable = candidateIsStable;
                        }
                    }

                    if (!haveBest || best is not { } bestValue)
                        throw new InvalidOperationException(
                            "No signed vmcreate-iso release found (a release needs the ISO plus " +
                            "vmcreate-<ver>.manifest.json and .manifest.json.sig). " +
                            "Check https://github.com/moerketh/vmcreate-iso/releases");

                    string selectedVersion = bestValue.Tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                        ? bestValue.Tag[1..]
                        : bestValue.Tag;
                    _logger.LogInformation("Selected vmcreate-iso release {Version} (stable={Stable}, signed releases seen: {Count}): {IsoUrl}",
                        selectedVersion, bestIsStable, signedCount, bestValue.IsoUrl);
                    return new SignedRelease(bestValue.Tag, selectedVersion, bestValue.IsoUrl, bestValue.ManifestUrl, bestValue.SignatureUrl);
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

        /// <summary>A release that publishes the three assets VMCreate
        /// needs to verify an ISO before boot.</summary>
        private sealed record SignedRelease(string Tag, string Version, string IsoUrl, string ManifestUrl, string SignatureUrl);

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

            // Both prereleases on the same core: compare suffixes per
            // semver — dot-separated identifiers, numeric where both parse
            // as numbers (alpha.10 > alpha.9), then fall back to publish
            // time.
            int cmp = ComparePrereleaseSuffixes(suffixA!, suffixB!);
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(publishedA, publishedB);
        }

        /// <summary>
        /// Semver prerelease-suffix ordering: dot-separated identifiers.
        /// Numeric identifiers compare numerically and rank BELOW
        /// alphanumeric ones ("alpha.1" &lt; "alpha.x"); when all compared
        /// identifiers tie, fewer fields means lower ("alpha" &lt;
        /// "alpha.1"). Positive = a is newer.
        /// </summary>
        internal static int ComparePrereleaseSuffixes(string suffixA, string suffixB)
        {
            string[] a = suffixA.Split('.');
            string[] b = suffixB.Split('.');
            int common = Math.Min(a.Length, b.Length);
            for (int i = 0; i < common; i++)
            {
                bool aNum = int.TryParse(a[i], out int aVal);
                bool bNum = int.TryParse(b[i], out int bVal);
                int cmp = aNum && bNum ? aVal.CompareTo(bVal)
                    : aNum ? -1 // numeric identifiers rank below alphanumeric
                    : bNum ? 1
                    : string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
            return a.Length.CompareTo(b.Length);
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

