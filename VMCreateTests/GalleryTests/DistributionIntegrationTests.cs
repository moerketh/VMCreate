using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using VMCreate;
using VMCreate.Gallery;
using VMCreate.Gallery.distributions;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// End-to-end integration tests that call real loaders against live servers,
    /// then verify every returned URL (DiskUri, ThumbnailUri, SymbolUri)
    /// responds with a non-error HTTP status (HEAD request).
    ///
    /// Run with:  dotnet test --filter TestCategory=Integration
    /// Skip with: dotnet test --filter TestCategory!=Integration
    /// </summary>
    [TestClass]
    [TestCategory("Integration")]
    public sealed class DistributionIntegrationTests
    {
        private static ServiceProvider _serviceProvider = null!;
        private static IHttpClientFactory _factory = null!;
        private static ILoggerFactory _loggerFactory = null!;

        // Shared HTTP client for URI verification — HEAD-only, follows redirects
        private static readonly HttpClient _http = new(
            new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            })
        { Timeout = TimeSpan.FromSeconds(30) };

        [ClassInitialize]
        public static void ClassInit(TestContext _)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpClient();
            _serviceProvider = services.BuildServiceProvider();
            _factory = _serviceProvider.GetRequiredService<IHttpClientFactory>();
            _loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
            _http.DefaultRequestHeaders.Add("User-Agent", "VMCreate/1.0");
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _http.Dispose();
            _serviceProvider.Dispose();
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Issues a HEAD request and asserts the server returns a success or redirect
        /// status (< 400). Empty / null URIs are silently skipped.
        /// For file:// URIs, checks that the local file exists instead.
        /// </summary>
        private static async Task AssertUriRespondsAsync(string uri, string context)
        {
            if (string.IsNullOrWhiteSpace(uri)) return;

            Assert.IsTrue(Uri.IsWellFormedUriString(uri, UriKind.Absolute),
                $"{context}: not a valid absolute URI: {uri}");

            var parsed = new Uri(uri);
            if (parsed.IsFile)
            {
                Assert.IsTrue(File.Exists(parsed.LocalPath),
                    $"{context}: local file not found: {parsed.LocalPath}");
                return;
            }

            using var req = new HttpRequestMessage(HttpMethod.Head, uri);
            var resp = await _http.SendAsync(req);

            Assert.IsTrue((int)resp.StatusCode < 400,
                $"{context}: HEAD returned {(int)resp.StatusCode} {resp.StatusCode} for\n  {uri}");
        }

        /// <summary>
        /// Verifies DiskUri, ThumbnailUri and SymbolUri for every item
        /// returned by the loader, all in parallel.
        /// </summary>
        private static async Task VerifyAllUrisAsync(List<GalleryItem> items, string loaderName)
        {
            Assert.IsNotNull(items, $"{loaderName}: null result");
            Assert.IsTrue(items.Count >= 1, $"{loaderName}: returned 0 items");

            var checks = new List<Task>();
            foreach (var item in items)
            {
                var n = $"{loaderName}[{item.Name}]";
                checks.Add(AssertUriRespondsAsync(item.DiskUri,      $"{n}.DiskUri"));
                checks.Add(AssertUriRespondsAsync(item.ThumbnailUri, $"{n}.ThumbnailUri"));
                checks.Add(AssertUriRespondsAsync(item.SymbolUri,    $"{n}.SymbolUri"));
            }
            await Task.WhenAll(checks);
        }

        // ════════════════════════════════════════════════════════════════════════
        // Static loaders — URLs are hardcoded, no HTTP needed to load items
        // ════════════════════════════════════════════════════════════════════════

        [TestMethod, Timeout(60_000)]
        public async Task Arch_AllUrisResolve()
            => await VerifyAllUrisAsync(await new Arch().LoadGalleryItems(), nameof(Arch));

        [TestMethod, Timeout(60_000)]
        public async Task NixOS_AllUrisResolve()
            => await VerifyAllUrisAsync(await new NixOS().LoadGalleryItems(), nameof(NixOS));

        [TestMethod, Timeout(60_000)]
        public async Task FedoraSilverblue_AllUrisResolve()
            => await VerifyAllUrisAsync(await new FedoraSilverblue().LoadGalleryItems(), nameof(FedoraSilverblue));

        [TestMethod, Timeout(60_000)]
        public async Task PwnCloudOS_AllUrisResolve()
            => await VerifyAllUrisAsync(await new PwnCloudOS().LoadGalleryItems(), nameof(PwnCloudOS));

        [TestMethod, Timeout(60_000)]
        public async Task LoadFedoraSecurityLab_AllUrisResolve()
            => await VerifyAllUrisAsync(await new FedoraSecurityLab().LoadGalleryItems(), nameof(FedoraSecurityLab));

        // ════════════════════════════════════════════════════════════════════════
        // HTTP loaders — live scrape then URI verification
        // ════════════════════════════════════════════════════════════════════════

        [TestMethod, Timeout(120_000)]
        public async Task BlackArch_AllUrisResolve()
            => await VerifyAllUrisAsync(
                await new BlackArch(_factory).LoadGalleryItems(),
                nameof(BlackArch));

        [TestMethod, Timeout(120_000)]
        public async Task LoadKaliCurrent_AllUrisResolve()
            => await VerifyAllUrisAsync(
                await new Kali(_factory).LoadGalleryItems(),
                nameof(Kali));

        [TestMethod, Timeout(120_000)]
        public async Task LoadParrot_AllUrisResolve()
            => await VerifyAllUrisAsync(
                await new Parrot(_factory).LoadGalleryItems(),
                nameof(Parrot));

        [TestMethod, Timeout(120_000), Ignore("ClearLinux infrastructure (clearlinux.org/cdn.download.clearlinux.org) has been discontinued by Intel")]
        public async Task ClearLinux_AllUrisResolve()
            => await VerifyAllUrisAsync(
                await new ClearLinux(_factory).LoadGalleryItems(),
                nameof(ClearLinux));

        [TestMethod, Timeout(120_000)]
        public async Task Ubuntu_AllUrisResolve()
        {
            var logger = _loggerFactory.CreateLogger<Ubuntu>();
            await VerifyAllUrisAsync(
                await new Ubuntu(logger, _factory).LoadGalleryItems(),
                nameof(Ubuntu));
        }

        [TestMethod, Timeout(120_000)]
        public async Task LoadPentooCurrent_AllUrisResolve()
            => await VerifyAllUrisAsync(
                await new PentooCurrent(_factory).LoadGalleryItems(),
                nameof(PentooCurrent));

        [TestMethod, Timeout(120_000)]
        public async Task LoadTails_AllUrisResolve()
            => await VerifyAllUrisAsync(
                await new Tails(_factory).LoadGalleryItems(),
                nameof(Tails));

        // ════════════════════════════════════════════════════════════════════════
        // Security — new distributions
        // ════════════════════════════════════════════════════════════════════════

        [TestMethod, Timeout(60_000)]
        public async Task REMnux_AllUrisResolve()
            => await VerifyAllUrisAsync(await new REMnux().LoadGalleryItems(), nameof(REMnux));

        [TestMethod, Timeout(60_000)]
        public async Task CAINE_AllUrisResolve()
            => await VerifyAllUrisAsync(await new CAINE().LoadGalleryItems(), nameof(CAINE));

        [TestMethod, Timeout(60_000)]
        public async Task Whonix_AllUrisResolve()
            => await VerifyAllUrisAsync(await new Whonix().LoadGalleryItems(), nameof(Whonix));

        [TestMethod, Timeout(60_000)]
        public async Task Tsurugi_AllUrisResolve()
            => await VerifyAllUrisAsync(await new Tsurugi().LoadGalleryItems(), nameof(Tsurugi));

        [TestMethod, Timeout(120_000)]
        public async Task LoadSecurityOnion_AllUrisResolve()
            => await VerifyAllUrisAsync(
                await new SecurityOnion(_factory).LoadGalleryItems(),
                nameof(SecurityOnion));

        // ════════════════════════════════════════════════════════════════════════
        // General — new distributions
        // ════════════════════════════════════════════════════════════════════════

        [TestMethod, Timeout(60_000)]
        public async Task FedoraWorkstation_AllUrisResolve()
            => await VerifyAllUrisAsync(await new FedoraWorkstation().LoadGalleryItems(), nameof(FedoraWorkstation));

        [TestMethod, Timeout(60_000)]
        public async Task OpenSuseTumbleweed_AllUrisResolve()
            => await VerifyAllUrisAsync(await new OpenSuseTumbleweed().LoadGalleryItems(), nameof(OpenSuseTumbleweed));

        [TestMethod, Timeout(60_000)]
        public async Task LinuxMint_AllUrisResolve()
            => await VerifyAllUrisAsync(await new LinuxMint().LoadGalleryItems(), nameof(LinuxMint));

        [TestMethod, Timeout(60_000)]
        public async Task AlpineLinux_AllUrisResolve()
            => await VerifyAllUrisAsync(await new AlpineLinux().LoadGalleryItems(), nameof(AlpineLinux));

        [TestMethod, Timeout(120_000)]
        public async Task Debian_AllUrisResolve()
            => await VerifyAllUrisAsync(
                await new Debian(_factory).LoadGalleryItems(),
                nameof(Debian));

        [TestMethod, Timeout(120_000)]
        public async Task RockyLinux_AllUrisResolve()
            => await VerifyAllUrisAsync(
                await new RockyLinux().LoadGalleryItems(),
                nameof(RockyLinux));
    }
}
