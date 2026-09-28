using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Loader for the REMnux malware-analysis OVA (Ubuntu-based).
    /// <para>
    /// Primary path scrapes the canonical OVA URL and its SHA-256 live from
    /// REMnux's docs page, so the gallery tracks new releases automatically.
    /// If the scrape fails (offline, docs restructured), the loader falls back
    /// to the pinned known-good pair below — self-updating AND never-breaking.
    /// Update the pinned pair when a new major release is published at
    /// https://docs.remnux.org.
    /// </para>
    /// </summary>
    public class REMnux : IGalleryLoader
    {
        private const string DocsUrl = "https://docs.remnux.org/install-distro/get-virtual-appliance.md";

        // ── Pinned fallback (last verified against docs.remnux.org) ──
        private const string PinnedVersion = "noble-202602";
        private const string PinnedOvaUrl = "https://download.remnux.org/202602/remnux-noble-amd64.ova";
        private const string PinnedSha256 = "1b10f522671d42b5fac60238d660871001ecdd94474aace55e0ef2b88e2bdecb";

        private static readonly Regex OvaLinkRegex = new(
            @"\[Download the general OVA file\.?\]\((https?://download\.remnux\.org/[^\s)]+?remnux-noble-amd64\.ova)\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Sha256Regex = new(
            @"\b([0-9a-f]{64})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly ILogger<REMnux> _logger;
        private readonly IHttpClientFactory _clientFactory;

        public REMnux(ILogger<REMnux> logger, IHttpClientFactory clientFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public REMnux(IHttpClientFactory clientFactory)
            : this(Microsoft.Extensions.Logging.Abstractions.NullLogger<REMnux>.Instance, clientFactory)
        {
        }

        /// <summary>
        /// Offline, deterministic constructor used by tests: never scrapes,
        /// always returns the pinned pair.
        /// </summary>
        public REMnux()
            : this(Microsoft.Extensions.Logging.Abstractions.NullLogger<REMnux>.Instance, OfflineClientFactory)
        {
        }

        /// <summary>
        /// An <see cref="IHttpClientFactory"/> whose client always fails, so the
        /// parameterless construction deterministically takes the pinned fallback.
        /// </summary>
        private static IHttpClientFactory OfflineClientFactory { get; } = CreateOfflineFactory();

        private static IHttpClientFactory CreateOfflineFactory()
        {
            var client = new HttpClient(new FailingHandler(), disposeHandler: true);
            return new SingletonClientFactory(client);
        }

        private sealed class FailingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => throw new HttpRequestException("Offline loader: pinned fallback only.");
        }

        private sealed class SingletonClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;
            public SingletonClientFactory(HttpClient client) => _client = client;
            public HttpClient CreateClient(string name) => _client;
        }

        public async Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
        {
            var (ovaUrl, sha256, version, isPinned) = await ResolveCurrentOvaAsync(cancellationToken);

            var logoUri = await GalleryIcons.ResolveLogoUriAsync(typeof(REMnux).Assembly, "remnux-logo.svg");
            var item = new GalleryItem
            {
                Name        = "REMnux",
                Publisher   = "REMnux Project",
                Description = $"REMnux is a Linux toolkit for reverse-engineering and analysing malware. Built on Ubuntu, it includes hundreds of tools for examining executables, documents, scripts and memory images (version {version}).",
                ThumbnailUri = logoUri,
                SymbolUri    = logoUri,
                DiskUri      = ovaUrl,
                ChecksumAlgorithm = "SHA256",
                Checksum     = sha256,
                SecureBoot   = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version      = version,
                LastUpdated  = DateTime.UtcNow.ToString("o"),
                InitialUsername = "remnux",
                InitialPassword = "malware",
                Category     = "Security"
            };
            return new List<GalleryItem> { item };
        }

        /// <summary>
        /// Scrapes the current general-OVA URL and its SHA-256 from the REMnux
        /// docs page. Returns the pinned fallback pair when the scrape fails or
        /// yields anything malformed.
        /// </summary>
        private async Task<(string OvaUrl, string Sha256, string Version, bool IsPinned)> ResolveCurrentOvaAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                var client = _clientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");

                var response = await client.GetAsync(DocsUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var markdown = await response.Content.ReadAsStringAsync(cancellationToken);

                var linkMatch = OvaLinkRegex.Match(markdown);
                if (!linkMatch.Success)
                {
                    _logger.LogWarning(
                        "REMnux docs no longer contain the general OVA link pattern — using pinned {PinnedOvaUrl}.",
                        PinnedOvaUrl);
                    return (PinnedOvaUrl, PinnedSha256, PinnedVersion, IsPinned: true);
                }
                var ovaUrl = linkMatch.Groups[1].Value;

                // The hash sits in the first fenced block after the link
                // ("Step 2: Confirm the Hash"); take the first 64-hex token
                // following the OVA link to avoid matching other tabs' hashes.
                var afterLink = markdown.Substring(linkMatch.Index);
                var hashMatch = Sha256Regex.Match(afterLink);
                if (!hashMatch.Success)
                {
                    _logger.LogWarning(
                        "REMnux docs page has no SHA-256 for the general OVA — using pinned fallback.");
                    return (PinnedOvaUrl, PinnedSha256, PinnedVersion, IsPinned: true);
                }
                var sha256 = hashMatch.Groups[1].Value.ToLowerInvariant();

                // Consistency check: the docs page lists exactly one general-OVA
                // link and one general-OVA hash, so a valid scrape either matches
                // the pinned pair exactly (no release since) or differs in BOTH
                // components (new release). Differing in exactly one component
                // means the docs layout changed — fall back to the pinned pair.
                bool urlIsPinned = string.Equals(ovaUrl, PinnedOvaUrl, StringComparison.OrdinalIgnoreCase);
                bool hashIsPinned = string.Equals(sha256, PinnedSha256, StringComparison.OrdinalIgnoreCase);
                if (urlIsPinned != hashIsPinned)
                {
                    _logger.LogWarning(
                        "REMnux docs scrape inconsistent (url pinned: {UrlIsPinned}, hash pinned: {HashIsPinned}) — using pinned fallback.",
                        urlIsPinned, hashIsPinned);
                    return (PinnedOvaUrl, PinnedSha256, PinnedVersion, IsPinned: true);
                }

                var version = ExtractVersion(ovaUrl);
                _logger.LogInformation(
                    "REMnux: using {Source} OVA {OvaUrl} (sha256 {Sha256}, version {Version}).",
                    urlIsPinned ? "pinned (matches live docs)" : "live-scraped", ovaUrl, sha256, version);
                return (ovaUrl, sha256, version, IsPinned: urlIsPinned);
            }
            catch (OperationCanceledException)
            {
                // The pinned fallback is synchronous and instant, so even a
                // cancelled scrape can still resolve without delay — the
                // static-loader contract requires results, not cancellation.
                return (PinnedOvaUrl, PinnedSha256, PinnedVersion, IsPinned: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to scrape REMnux docs at {DocsUrl} — using pinned {PinnedOvaUrl}.",
                    DocsUrl, PinnedOvaUrl);
                return (PinnedOvaUrl, PinnedSha256, PinnedVersion, IsPinned: true);
            }
        }

        /// <summary>
        /// Determines the version label from the OVA URL: the release stamp
        /// lives in the path directory (…/202602/remnux-noble-amd64.ova), the
        /// filename itself is versionless, so combine series + stamp →
        /// "noble-202602".
        /// </summary>
        private static string ExtractVersion(string ovaUrl)
        {
            var match = Regex.Match(ovaUrl, @"download\.remnux\.org/(\d{6})/remnux-(noble)-amd64\.ova", RegexOptions.IgnoreCase);
            return match.Success ? $"{match.Groups[2].Value}-{match.Groups[1].Value}" : PinnedVersion;
        }
    }
}
