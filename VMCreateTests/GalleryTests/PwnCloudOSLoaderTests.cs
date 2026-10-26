using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using VMCreate.Gallery;
using VMCreate.Gallery.distributions;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Regression tests for the PwnCloudOS loader's checksum scrape. The
    /// publisher publishes SHA-256 hashes in an HTML table; the extraction
    /// must survive both the plain-text row format and the styled live page
    /// (whose &lt;td&gt; attributes contain hex-looking values) so the item
    /// badges as "✓ verified" instead of "⚠ unverified".
    /// </summary>
    [TestClass]
    public sealed class PwnCloudOSLoaderTests
    {
        private const string CurrentHash =
            "2f6b12183003bee17066fe8066acc2f4af74262456828018197647420360dd55";

        private static IHttpClientFactory FactoryReturning(string html)
        {
            var mock = new Mock<IHttpClientFactory>();
            mock.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(new FixedHandler(html), disposeHandler: false));
            return mock.Object;
        }

        private sealed class FixedHandler : HttpMessageHandler
        {
            private readonly string _html;
            public FixedHandler(string html) => _html = html;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_html)
                });
        }

        [TestMethod]
        public async Task ChecksumScrape_StyledLivePage_ExtractsVmHash()
        {
            // Format of the live pwncloudos.pwnedlabs.io page (2026-10): the
            // table cells carry inline styles with hex-like values (#cccccc,
            // 16px, 0.85em) between the label and the hash. The loader strips
            // tags before matching, so these must not disturb extraction.
            var html = $"""
                <table>
                  <thead><tr><th><span>Operating System Image</span></th><th><span>SHA256 Hash</span></th></tr></thead>
                  <tbody>
                  <tr>
                    <td style="padding: 16px 20px; color: #ffffff; border-bottom: 1px solid rgba(255,255,255,0.05);">AMD64 Virtual Machine</td>
                    <td style="padding: 16px 20px; color: #cccccc; font-family: monospace; font-size: 0.85em;">{CurrentHash}</td>
                  </tr>
                  <tr>
                    <td style="padding: 16px 20px; color: #ffffff;">AMD64 ISO Image</td>
                    <td style="padding: 16px 20px; color: #cccccc; font-family: monospace;">8e7fe6765112d999cb47bedbe216c0940f7e686d6e905c0ebefee054438d9899</td>
                  </tr>
                  </tbody>
                </table>
                """;

            var item = (await new PwnCloudOS(FactoryReturning(html)).LoadGalleryItems()).Single();

            Assert.AreEqual(CurrentHash, item.RuntimeChecksum,
                "the VM image hash (not the ISO's) must be scraped from the styled live page");
            Assert.AreEqual("SHA256", item.ChecksumAlgorithm);
            Assert.IsNull(item.Checksum, "a publisher-page digest must never occupy the pinned Checksum field");
            Assert.IsFalse(item.HasChecksum, "a runtime-scraped digest must not badge as verified");
        }

        [TestMethod]
        public async Task ChecksumScrape_PlainTextRow_ExtractsVmHash()
        {
            // Original 2026-04 format the extraction was written against.
            var html = $"""
                <p>AMD64 Virtual Machine | {CurrentHash}</p>
                """;

            var item = (await new PwnCloudOS(FactoryReturning(html)).LoadGalleryItems()).Single();

            Assert.AreEqual(CurrentHash, item.RuntimeChecksum);
            Assert.AreEqual("SHA256", item.ChecksumAlgorithm);
            Assert.IsNull(item.Checksum, "a publisher-page digest must never occupy the pinned Checksum field");
        }

        [TestMethod]
        public async Task ChecksumScrape_MissingRow_LeavesItemUnverifiedNotBroken()
        {
            var html = "<html><body>No downloads here</body></html>";

            var items = await new PwnCloudOS(FactoryReturning(html)).LoadGalleryItems();

            Assert.AreEqual(1, items.Count, "a page restructure must degrade to an unverified item, not an empty gallery");
            var item = items[0];
            Assert.IsNull(item.Checksum);
            Assert.IsFalse(item.HasChecksum);
            Assert.IsFalse(string.IsNullOrEmpty(item.DiskUri));
        }

        [TestMethod]
        public async Task ChecksumScrape_HttpFailure_LeavesItemUnverifiedNotBroken()
        {
            var mock = new Mock<IHttpClientFactory>();
            mock.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(
                    new ThrowingHandler(), disposeHandler: false));

            var items = await new PwnCloudOS(mock.Object).LoadGalleryItems();

            Assert.AreEqual(1, items.Count, "an unreachable checksum page must not break gallery enumeration");
            Assert.IsFalse(items[0].HasChecksum);
        }

        [TestMethod]
        public async Task LoadGalleryItems_IsRecommendedSecurityItem()
        {
            var item = (await new PwnCloudOS(FactoryReturning(
                "<html><body>none</body></html>")).LoadGalleryItems()).Single();

            Assert.IsTrue(item.IsRecommended);
            Assert.AreEqual("Security", item.Category);
            Assert.IsTrue(item.HasTag("pwncloudos"));
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => throw new HttpRequestException("offline");
        }
    }
}