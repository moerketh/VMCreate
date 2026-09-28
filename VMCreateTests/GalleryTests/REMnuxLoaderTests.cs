using Microsoft.Extensions.Logging.Abstractions;
using System.Linq;
using System.Net;
using System.Net.Http;
using Moq;
using VMCreate.Gallery;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Tests for the REMnux loader's live-scrape-with-pinned-fallback
    /// behaviour and the Whonix SHA-512 wiring.
    /// </summary>
    [TestClass]
    public sealed class REMnuxLoaderTests
    {
        private const string PinnedOvaUrl = "https://download.remnux.org/202602/remnux-noble-amd64.ova";
        private const string PinnedSha256 = "1b10f522671d42b5fac60238d660871001ecdd94474aace55e0ef2b88e2bdecb";

        /// <summary>
        /// Mirrors the real structure of docs.remnux.org's get-virtual-appliance
        /// page: tabbed download links (General OVA first) followed by tabbed
        /// hashes, with the general OVA's hash first.
        /// </summary>
        private static string DocsMarkdown(string ovaUrl, string generalHash) => string.Join("\n",
            "# Get the Virtual Appliance",
            "{% tabs %}",
            "{% tab title=\"General OVA\" %}",
            $"[Download the general OVA file.]({ovaUrl})",
            "{% endtab %}",
            "{% tab title=\"VirtualBox OVA\" %}",
            "[Download the VirtualBox OVA file.](https://download.remnux.org/202602/remnux-noble-amd64-virtualbox.ova)",
            "{% endtab %}",
            "{% endtabs %}",
            "## Step 2: Confirm the Hash of the Downloaded File",
            "{% tabs %}",
            "{% tab title=\"General OVA Hash\" %}",
            "```",
            generalHash,
            "```",
            "{% endtab %}",
            "{% tab title=\"VirtualBox OVA Hash\" %}",
            "```",
            "1ba3196ad82f3536954404546aa510ff09cf0c6c0567847272a9389e2e160a7b",
            "```",
            "{% endtab %}",
            "{% endtabs %}");

        private static IHttpClientFactory FactoryFor(string content) => FactoryFor(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });

        private static IHttpClientFactory FailingFactory() => FactoryFor(
            _ => throw new HttpRequestException("network unreachable"));

        private static IHttpClientFactory FactoryFor(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(new ResponderHandler(responder), disposeHandler: false));
            return factory.Object;
        }

        private sealed class ResponderHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public ResponderHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_responder(request));
        }

        // ── Live-scrape path ─────────────────────────────────────────────────

        [TestMethod]
        public async Task Scrape_NewRelease_UsesLiveUrlAndHash()
        {
            const string liveUrl = "https://download.remnux.org/202610/remnux-noble-amd64.ova";
            const string liveHash = "aaaa6666aaaa6666aaaa6666aaaa6666aaaa6666aaaa6666aaaa6666aaaa6666";
            var loader = new REMnux(NullLogger<REMnux>.Instance, FactoryFor(DocsMarkdown(liveUrl, liveHash)));

            var items = await loader.LoadGalleryItems();

            var item = items.Single();
            Assert.AreEqual(liveUrl, item.DiskUri, "must use the scraped URL for a new release");
            Assert.AreEqual(liveHash, item.Checksum, "must use the scraped hash for a new release");
            Assert.AreEqual("noble-202610", item.Version, "version must be derived from the scraped URL");
        }

        [TestMethod]
        public async Task Scrape_MatchesPinnedPair_UsesScrapedValues()
        {
            // No release since the pin: the scrape returns exactly the pinned
            // pair, which is still a consistent live result.
            var loader = new REMnux(NullLogger<REMnux>.Instance, FactoryFor(DocsMarkdown(PinnedOvaUrl, PinnedSha256)));

            var item = (await loader.LoadGalleryItems()).Single();

            Assert.AreEqual(PinnedOvaUrl, item.DiskUri);
            Assert.AreEqual(PinnedSha256, item.Checksum);
            Assert.AreEqual("noble-202602", item.Version);
        }

        // ── Pinned-fallback path ─────────────────────────────────────────────

        [TestMethod]
        public async Task Scrape_NetworkFailure_FallsBackToPinnedPair()
        {
            var loader = new REMnux(NullLogger<REMnux>.Instance, FailingFactory());

            var item = (await loader.LoadGalleryItems()).Single();

            Assert.AreEqual(PinnedOvaUrl, item.DiskUri);
            Assert.AreEqual(PinnedSha256, item.Checksum);
        }

        [TestMethod]
        public async Task Scrape_LinkPatternMissing_FallsBackToPinnedPair()
        {
            // Docs restructured: no general-OVA link at all.
            var loader = new REMnux(NullLogger<REMnux>.Instance,
                FactoryFor("# Moved\nThe download now lives elsewhere."));

            var item = (await loader.LoadGalleryItems()).Single();

            Assert.AreEqual(PinnedOvaUrl, item.DiskUri);
            Assert.AreEqual(PinnedSha256, item.Checksum);
        }

        [TestMethod]
        public async Task Scrape_HashMissing_FallsBackToPinnedPair()
        {
            // Docs link present but the hash section vanished — no 64-hex
            // token after the link.
            const string noHash = "# Get the Virtual Appliance\n[Download the general OVA file.](https://download.remnux.org/202602/remnux-noble-amd64.ova)";
            var loader = new REMnux(NullLogger<REMnux>.Instance, FactoryFor(noHash));

            var item = (await loader.LoadGalleryItems()).Single();

            Assert.AreEqual(PinnedOvaUrl, item.DiskUri);
            Assert.AreEqual(PinnedSha256, item.Checksum);
        }

        [TestMethod]
        public async Task Scrape_InconsistentPair_FallsBackToPinnedPair()
        {
            // New URL but the pinned (stale) hash scraped alongside it, or
            // vice versa — exactly one component changed means the layout
            // changed, not a release. Never mix provenance.
            const string newUrl = "https://download.remnux.org/202610/remnux-noble-amd64.ova";
            var loader = new REMnux(NullLogger<REMnux>.Instance, FactoryFor(DocsMarkdown(newUrl, PinnedSha256)));

            var item = (await loader.LoadGalleryItems()).Single();

            Assert.AreEqual(PinnedOvaUrl, item.DiskUri, "must not pair a new URL with a stale hash");
            Assert.AreEqual(PinnedSha256, item.Checksum);
        }

        [TestMethod]
        public async Task ParameterlessCtor_IsDeterministicPinnedPair()
        {
            // The offline constructor must never touch the network.
            var item = (await new REMnux().LoadGalleryItems()).Single();

            Assert.AreEqual(PinnedOvaUrl, item.DiskUri);
            Assert.AreEqual(PinnedSha256, item.Checksum);
            Assert.AreEqual("SHA256", item.ChecksumAlgorithm);
        }

        // ── Whonix SHA-512 wiring ────────────────────────────────────────────

        [TestMethod]
        public async Task Whonix_ShipsSha512ChecksumUri()
        {
            var item = (await new Whonix().LoadGalleryItems()).Single();

            Assert.IsFalse(string.IsNullOrEmpty(item.ChecksumUri),
                "Whonix must wire its .sha512sums sidecar so downloads verify");
            Assert.AreEqual("SHA512", item.ChecksumAlgorithm,
                "Whonix publishes SHA-512 only — the loader must not claim SHA-256");
            StringAssert.EndsWith(item.ChecksumUri, ".ova.sha512sums",
                "checksum URI must be the sidecar next to the OVA");
        }
    }
}
