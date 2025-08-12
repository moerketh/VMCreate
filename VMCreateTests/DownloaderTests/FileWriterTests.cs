using Microsoft.Extensions.Logging;
using Moq;
using VMCreate;

namespace VMCreateTests
{
    [TestClass]
    public class FileWriterTests
    {
        private Mock<ILogger<FileWriter>> _mockLogger;
        private FileWriter _writer;

        [TestInitialize]
        public void Setup()
        {
            _mockLogger = new Mock<ILogger<FileWriter>>();
            _writer = new FileWriter(_mockLogger.Object);
        }

        [TestMethod]
        public void TryGetCachedFile_FileExists_ReturnsTrueAndPath()
        {
            // Arrange
            var filePath = Path.Combine(Path.GetTempPath(), "testfile.zip");
            File.WriteAllBytes(filePath, new byte[0]); // Create temp file
            try
            {
                // Act
                bool exists = _writer.TryGetCachedFile(filePath, out string cachedPath);

                // Assert
                Assert.IsTrue(exists);
                Assert.AreEqual(filePath, cachedPath);
                _mockLogger.Verify(l => l.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Using cached file")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Once);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [TestMethod]
        public void TryGetCachedFile_FileDoesNotExist_ReturnsFalse()
        {
            // Arrange
            var filePath = Path.Combine(Path.GetTempPath(), "nonexistent.zip");

            // Act
            bool exists = _writer.TryGetCachedFile(filePath, out string cachedPath);

            // Assert
            Assert.IsFalse(exists);
            Assert.IsNull(cachedPath);
        }

        [TestMethod]
        public async Task WriteAsync_SuccessfulWrite_ReportsProgress()
        {
            // Arrange
            var sourceStream = new MemoryStream(new byte[1024 * 1024]); // 1MB to simulate
            var filePath = Path.GetTempFileName();
            var contentLength = 1024 * 1024L;
            var finalUri = "http://example.com/file.zip";
            var progressReports = new List<CreateVMProgressInfo>();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();
            mockProgress.Setup(p => p.Report(It.IsAny<CreateVMProgressInfo>())).Callback<CreateVMProgressInfo>(progressReports.Add);

            // Act
            await _writer.WriteAsync(sourceStream, filePath, contentLength, finalUri, mockProgress.Object, CancellationToken.None);

            // Assert
            Assert.IsTrue(File.Exists(filePath));
            Assert.IsTrue(progressReports.Count > 0); // At least one report
            Assert.AreEqual(finalUri, progressReports[0].URI);
            Assert.IsTrue(progressReports[^1].ProgressPercentage == 100 || progressReports[^1].ProgressPercentage > 0); // Last should be high
            File.Delete(filePath);
        }

        [TestMethod]
        public async Task WriteAsync_FastWriteUnder1Second_Reports0And100Percent()
        {
            // Arrange (small data to simulate fast write)
            var sourceStream = new MemoryStream(new byte[10]); // Very small
            var filePath = Path.GetTempFileName();
            var contentLength = 10L;
            var finalUri = "http://example.com/small.zip";
            var progressReports = new List<CreateVMProgressInfo>();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();
            mockProgress.Setup(p => p.Report(It.IsAny<CreateVMProgressInfo>())).Callback<CreateVMProgressInfo>(progressReports.Add);

            // Act
            await _writer.WriteAsync(sourceStream, filePath, contentLength, finalUri, mockProgress.Object, CancellationToken.None);

            // Assert
            Assert.IsTrue(File.Exists(filePath));
            Assert.AreEqual(2, progressReports.Count);
            Assert.AreEqual(0, progressReports[0].ProgressPercentage);
            Assert.AreEqual(100, progressReports[1].ProgressPercentage);
            Assert.AreEqual(finalUri, progressReports[0].URI);
            Assert.AreEqual(finalUri, progressReports[1].URI);
            File.Delete(filePath);
        }

        [TestMethod]
        [ExpectedException(typeof(TaskCanceledException))]
        public async Task WriteAsync_CancellationRequested_ThrowsException()
        {
            // Arrange
            var sourceStream = new MemoryStream(new byte[1024]);
            var filePath = Path.GetTempFileName();
            var cts = new CancellationTokenSource();
            cts.Cancel(); // Immediate cancel
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            // Act
            await _writer.WriteAsync(sourceStream, filePath, 1024L, "http://example.com", mockProgress.Object, cts.Token);
        }

        [TestMethod]
        public async Task WriteAsync_NoContentLength_Reports0PercentUntilComplete()
        {
            // Arrange
            var sourceStream = new MemoryStream(new byte[1024]);
            var filePath = Path.GetTempFileName();
            long? contentLength = null;
            var finalUri = "http://example.com/chunked.zip";
            var progressReports = new List<CreateVMProgressInfo>();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();
            mockProgress.Setup(p => p.Report(It.IsAny<CreateVMProgressInfo>())).Callback<CreateVMProgressInfo>(progressReports.Add);

            // Act
            await _writer.WriteAsync(sourceStream, filePath, contentLength, finalUri, mockProgress.Object, CancellationToken.None);

            // Assert
            Assert.AreEqual(2, progressReports.Count);
            Assert.AreEqual(0, progressReports[0].ProgressPercentage);
            Assert.AreEqual(100, progressReports[1].ProgressPercentage);
            File.Delete(filePath);
        }
    }
}
