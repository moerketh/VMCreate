using Microsoft.Extensions.Logging;
using Moq;
using VMCreate.Gallery;

namespace VMCreate.Tests.GalleryTests
{
    [TestClass]
    public sealed class AggregateGalleryLoaderTests
    {
        private Mock<ILogger<AggregateGalleryLoader>> _mockLogger;

        [TestInitialize]
        public void Setup()
        {
            _mockLogger = new Mock<ILogger<AggregateGalleryLoader>>();
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static Mock<IGalleryLoader> MakeLoader(IEnumerable<GalleryItem> items)
        {
            var mock = new Mock<IGalleryLoader>();
            mock.Setup(l => l.LoadGalleryItems(It.IsAny<CancellationToken>()))
                .ReturnsAsync(items.ToList());
            return mock;
        }

        private static GalleryItem MakeItem(string name) =>
            new GalleryItem { Name = name, DiskUri = "https://example.com/" + name };

        // ── Tests ─────────────────────────────────────────────────────────────────

        [TestMethod]
        public async Task LoadGalleryItems_MergesResultsFromAllLoaders()
        {
            var itemA = MakeItem("A");
            var itemB = MakeItem("B");
            var loaderA = MakeLoader([itemA]);
            var loaderB = MakeLoader([itemB]);

            var aggregate = new AggregateGalleryLoader(
                _mockLogger.Object,
                [loaderA.Object, loaderB.Object]);

            var result = await aggregate.LoadGalleryItems();

            Assert.AreEqual(2, result.Count);
            CollectionAssert.Contains(result, itemA);
            CollectionAssert.Contains(result, itemB);
        }

        [TestMethod]
        public async Task LoadGalleryItems_OneLoaderThrows_OthersStillReturn()
        {
            var goodItem = MakeItem("Good");
            var goodLoader = MakeLoader([goodItem]);

            var badLoader = new Mock<IGalleryLoader>();
            badLoader.Setup(l => l.LoadGalleryItems(It.IsAny<CancellationToken>()))
                     .ThrowsAsync(new InvalidOperationException("Network error"));

            var aggregate = new AggregateGalleryLoader(
                _mockLogger.Object,
                [goodLoader.Object, badLoader.Object]);

            var result = await aggregate.LoadGalleryItems();

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Good", result[0].Name);
        }

        [TestMethod]
        public async Task LoadGalleryItems_LoaderReturnsNull_TreatedAsEmpty()
        {
            var nullLoader = new Mock<IGalleryLoader>();
            nullLoader.Setup(l => l.LoadGalleryItems(It.IsAny<CancellationToken>()))
                      .ReturnsAsync((List<GalleryItem>)null);

            var aggregate = new AggregateGalleryLoader(
                _mockLogger.Object,
                [nullLoader.Object]);

            var result = await aggregate.LoadGalleryItems();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public async Task LoadGalleryItems_NoLoaders_ReturnsEmptyList()
        {
            var aggregate = new AggregateGalleryLoader(_mockLogger.Object, []);

            var result = await aggregate.LoadGalleryItems();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public async Task LoadGalleryItems_CancellationRequested_PropagatesCancellation()
        {
            using var cts = new CancellationTokenSource();

            var slowLoader = new Mock<IGalleryLoader>();
            slowLoader.Setup(l => l.LoadGalleryItems(It.IsAny<CancellationToken>()))
                      .Returns(async (CancellationToken ct) =>
                      {
                          await Task.Delay(Timeout.Infinite, ct);
                          return new List<GalleryItem>();
                      });

            var aggregate = new AggregateGalleryLoader(
                _mockLogger.Object,
                [slowLoader.Object]);

            cts.CancelAfter(50);

            try
            {
                await aggregate.LoadGalleryItems(cts.Token);
                Assert.Fail("Expected OperationCanceledException was not thrown.");
            }
            catch (OperationCanceledException)
            {
                // Correct — test passes (TaskCanceledException is also acceptable
                // because it inherits from OperationCanceledException).
            }
        }

        [TestMethod]
        public async Task LoadGalleryItems_CancellationTokenPassedToLoaders()
        {
            var capturedToken = CancellationToken.None;
            using var cts = new CancellationTokenSource();

            var loader = new Mock<IGalleryLoader>();
            loader.Setup(l => l.LoadGalleryItems(It.IsAny<CancellationToken>()))
                  .Callback((CancellationToken ct) => capturedToken = ct)
                  .ReturnsAsync(new List<GalleryItem>());

            var aggregate = new AggregateGalleryLoader(
                _mockLogger.Object,
                [loader.Object]);

            await aggregate.LoadGalleryItems(cts.Token);

            Assert.AreEqual(cts.Token, capturedToken);
        }

        [TestMethod]
        public async Task LoadGalleryItems_MultipleLoadersReturnEmpty_ReturnsEmptyList()
        {
            var loaderA = MakeLoader([]);
            var loaderB = MakeLoader([]);

            var aggregate = new AggregateGalleryLoader(
                _mockLogger.Object,
                [loaderA.Object, loaderB.Object]);

            var result = await aggregate.LoadGalleryItems();

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public async Task LoadGalleryItems_AllLoadersFail_ReturnsEmptyList()
        {
            var bad1 = new Mock<IGalleryLoader>();
            bad1.Setup(l => l.LoadGalleryItems(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("Timeout"));

            var bad2 = new Mock<IGalleryLoader>();
            bad2.Setup(l => l.LoadGalleryItems(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("DNS failure"));

            var aggregate = new AggregateGalleryLoader(
                _mockLogger.Object,
                [bad1.Object, bad2.Object]);

            var result = await aggregate.LoadGalleryItems();

            Assert.AreEqual(0, result.Count);
        }
    }
}
