using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using VMCreate.Gallery;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Tests for the gallery status surfacing: loader-failure tracking in
    /// the aggregate loader and the load summary it produces.
    /// </summary>
    [TestClass]
    public sealed class GalleryStatusTests
    {
        private static GalleryItem Item(string name) => new()
        {
            Name = name,
            DiskUri = $"https://example.com/{name}.iso",
            Publisher = "p",
            Description = "d",
            EnhancedSessionTransportType = "HvSocket",
            Version = "1.0"
        };

        [TestMethod]
        public async Task AggregateLoader_TracksFailedLoaderNames()
        {
            var aggregate = new AggregateGalleryLoader(NullLogger<AggregateGalleryLoader>.Instance,
                new IGalleryLoader[]
                {
                    new OkLoader(new List<GalleryItem> { Item("a") }),
                    new BrokenLoader(),
                    new EmptyLoader()
                });

            var items = await aggregate.LoadGalleryItems();

            Assert.AreEqual(1, items.Count, "the working loader's item must survive the failures");
            CollectionAssert.AreEquivalent(new[] { "BrokenLoader", "EmptyLoader" },
                aggregate.FailedLoaderNames.ToList(),
                "throwing and empty loaders must both be reported");
            Assert.AreEqual(3, aggregate.LoaderCount);
        }

        [TestMethod]
        public async Task GalleryService_ReturnsSummaryFromAggregate()
        {
            var aggregate = new AggregateGalleryLoader(
                NullLogger<AggregateGalleryLoader>.Instance,
                new IGalleryLoader[] { new BrokenLoader() });
            var service = new GalleryService(
                new InMemoryCache(), aggregate, NullLogger<GalleryService>.Instance);

            var summary = await service.LoadFromSourcesStreamingAsync(
                new HashSet<(string, string)>(), _ => { }, CancellationToken.None);

            Assert.AreEqual(1, summary.LoaderCount);
            CollectionAssert.AreEqual(new[] { "BrokenLoader" }, summary.FailedLoaders.ToList());
        }

        [TestMethod]
        public async Task GalleryService_NonAggregateLoader_ReportsSingleHealthyLoader()
        {
            var service = new GalleryService(
                new InMemoryCache(),
                new StubLoader("Solo", new List<GalleryItem> { Item("a") }),
                NullLogger<GalleryService>.Instance);

            var summary = await service.LoadFromSourcesStreamingAsync(
                new HashSet<(string, string)>(), _ => { }, CancellationToken.None);

            Assert.AreEqual(1, summary.LoaderCount);
            Assert.AreEqual(0, summary.FailedLoaders.Count);
        }

        [TestMethod]
        public void ComposeStatusLine_NoFailures_ReportsCountOnly()
        {
            var (text, toolTip) = GalleryLoadSummary.ComposeStatusLine(12,
                new GalleryLoadSummary { LoaderCount = 12 });

            Assert.AreEqual("12 images · live from distribution sites", text);
            Assert.IsNull(toolTip);
        }

        [TestMethod]
        public void ComposeStatusLine_WithFailures_ReportsFailuresAndToolTip()
        {
            var (text, toolTip) = GalleryLoadSummary.ComposeStatusLine(10,
                new GalleryLoadSummary { LoaderCount = 12, FailedLoaders = new[] { "Tails", "Kali" } });

            Assert.AreEqual("10 images · live from distribution sites · 2 of 12 loaders failed", text);
            StringAssert.Contains(toolTip ?? "", "Tails");
            StringAssert.Contains(toolTip ?? "", "Kali");
        }

        // ── GalleryItem badge properties (card checksum / deploy semantics) ──

        [TestMethod]
        public void HasChecksum_TrueOnlyForPinnedInlineHash()
        {
            var inline = new GalleryItem { Checksum = "deadbeef" };
            var viaUri = new GalleryItem { ChecksumUri = "https://example.com/SHA256SUMS" };
            var none = new GalleryItem();

            Assert.IsTrue(inline.HasChecksum, "pinned inline hash must badge as verified");
            // A checksum fetched from the same mirror as the image only proves
            // transport consistency — it must not badge as publisher-verified.
            Assert.IsFalse(viaUri.HasChecksum, "mirror-served checksum file must not badge as verified");
            Assert.IsFalse(none.HasChecksum, "no checksum at all must badge as unverified");
            Assert.IsTrue(none.HasNoChecksum, "HasNoChecksum is the XAML-facing inverse");
            Assert.IsTrue(viaUri.HasNoChecksum, "mirror-served checksum file badges unverified");
        }

        [TestMethod]
        public void DeploySemantics_MapsFormatsToUserVocabulary()
        {
            Assert.AreEqual("boots as-is", new GalleryItem { DiskUri = "https://x/i.vhdx" }.DeploySemantics);
            Assert.AreEqual("boots as-is", new GalleryItem { DiskUri = "https://x/i.vhd" }.DeploySemantics);
            Assert.AreEqual("converts", new GalleryItem { DiskUri = "https://x/i.ova" }.DeploySemantics);
            Assert.AreEqual("converts", new GalleryItem { DiskUri = "https://x/i.qcow2" }.DeploySemantics);
            Assert.AreEqual("converts", new GalleryItem { DiskUri = "https://x/i.vmdk.xz" }.DeploySemantics);
            Assert.AreEqual("manual install", new GalleryItem { DiskUri = "https://x/i.iso" }.DeploySemantics);
        }

        private sealed class StubLoader : IGalleryLoader
        {
            private readonly List<GalleryItem>? _items;
            private readonly bool _throws;

            public StubLoader(string name, List<GalleryItem>? items = null, bool throws = false)
            {
                _items = items;
                _throws = throws;
            }

            public Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
                => _throws ? throw new HttpRequestStubException("boom") : Task.FromResult(_items!);
        }

        private sealed class OkLoader : IGalleryLoader
        {
            private readonly List<GalleryItem> _items;
            public OkLoader(List<GalleryItem> items) => _items = items;
            public Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
                => Task.FromResult(_items);
        }

        private sealed class BrokenLoader : IGalleryLoader
        {
            public Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
                => throw new HttpRequestStubException("boom");
        }

        private sealed class EmptyLoader : IGalleryLoader
        {
            public Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default)
                => Task.FromResult(new List<GalleryItem>());
        }

        private sealed class HttpRequestStubException : Exception
        {
            public HttpRequestStubException(string message) : base(message) { }
        }

        private sealed class InMemoryCache : IGalleryCache
        {
            public bool TryLoadCache(out List<GalleryItem> items) { items = new(); return false; }
            public void SaveCache(List<GalleryItem> items) { }
        }
    }
}
