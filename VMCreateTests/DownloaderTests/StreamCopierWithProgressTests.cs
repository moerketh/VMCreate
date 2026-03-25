using Moq;

namespace VMCreate.Tests
{
    [TestClass]
    public class StreamCopierWithProgressTests
    {
        private StreamCopierWithProgress _copier;

        [TestInitialize]
        public void Setup()
        {
            _copier = new StreamCopierWithProgress();
        }

        [TestMethod]
        public async Task CopyAsync_SuccessfulCopy_ReportsProgress()
        {
            // Arrange
            var sourceStream = new MemoryStream(new byte[1024 * 1024]); // 1MB to simulate
            var destinationStream = new MemoryStream();
            var contentLength = 1024 * 1024L;
            var finalUri = "http://example.com/file.zip";
            var progressReports = new List<CreateVMProgressInfo>();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();
            mockProgress.Setup(p => p.Report(It.IsAny<CreateVMProgressInfo>())).Callback<CreateVMProgressInfo>(progressReports.Add);

            // Act
            await _copier.CopyAsync(sourceStream, destinationStream, contentLength, finalUri, mockProgress.Object, CancellationToken.None);

            // Assert
            Assert.IsTrue(progressReports.Count >= 2); // At least initial and final
            Assert.AreEqual(finalUri, progressReports[0].URI);
            Assert.AreEqual(0, progressReports[0].ProgressPercentage);
            Assert.AreEqual(100, progressReports[^1].ProgressPercentage);
        }

        [TestMethod]
        public async Task CopyAsync_FastCopyUnder1Second_Reports0And100Percent()
        {
            // Arrange
            var sourceStream = new MemoryStream(new byte[10]); // Very small
            var destinationStream = new MemoryStream();
            var contentLength = 10L;
            var finalUri = "http://example.com/small.zip";
            var progressReports = new List<CreateVMProgressInfo>();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();
            mockProgress.Setup(p => p.Report(It.IsAny<CreateVMProgressInfo>())).Callback<CreateVMProgressInfo>(progressReports.Add);

            // Act
            await _copier.CopyAsync(sourceStream, destinationStream, contentLength, finalUri, mockProgress.Object, CancellationToken.None);

            // Assert
            Assert.AreEqual(2, progressReports.Count); // Initial 0% and final 100%
            Assert.AreEqual(0, progressReports[0].ProgressPercentage);
            Assert.AreEqual(100, progressReports[1].ProgressPercentage);
            Assert.AreEqual(finalUri, progressReports[0].URI);
            Assert.AreEqual(finalUri, progressReports[1].URI);
        }

        [TestMethod]
        public async Task CopyAsync_CancellationRequested_ThrowsException()
        {
            // Arrange
            var sourceStream = new MemoryStream(new byte[1024]);
            var destinationStream = new MemoryStream();
            var cts = new CancellationTokenSource();
            cts.Cancel(); // Immediate cancel
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            // Act
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => _copier.CopyAsync(sourceStream, destinationStream, 1024L, "http://example.com", mockProgress.Object, cts.Token));
        }

        [TestMethod]
        public async Task CopyAsync_NoContentLength_Reports0PercentUntilComplete()
        {
            // Arrange
            var sourceStream = new MemoryStream(new byte[1024]);
            var destinationStream = new MemoryStream();
            long? contentLength = null;
            var finalUri = "http://example.com/chunked.zip";
            var progressReports = new List<CreateVMProgressInfo>();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();
            mockProgress.Setup(p => p.Report(It.IsAny<CreateVMProgressInfo>())).Callback<CreateVMProgressInfo>(progressReports.Add);

            // Act
            await _copier.CopyAsync(sourceStream, destinationStream, contentLength, finalUri, mockProgress.Object, CancellationToken.None);

            // Assert
            Assert.IsTrue(progressReports.Count >= 2); // Initial and final
            Assert.AreEqual(0, progressReports[0].ProgressPercentage);
            Assert.AreEqual(100, progressReports[^1].ProgressPercentage); // 100% on completion
        }
    }
}
