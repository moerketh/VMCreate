using Microsoft.Extensions.Logging;
using Moq;
using System.Diagnostics;

namespace VMCreate.Tests
{
    [TestClass]
    public class HttpFileDownloaderTests
    {
        private Mock<ILogger<HttpFileDownloader>> _mockLogger;
        private Mock<IHttpStreamProvider> _mockStreamProvider;
        private Mock<IFileWriter> _mockFileWriter;
        private HttpFileDownloader _downloader;

        [TestInitialize]
        public void Setup()
        {
            _mockLogger = new Mock<ILogger<HttpFileDownloader>>();
            _mockStreamProvider = new Mock<IHttpStreamProvider>();
            _mockFileWriter = new Mock<IFileWriter>();
            _downloader = new HttpFileDownloader(_mockLogger.Object, _mockStreamProvider.Object, _mockFileWriter.Object);
        }

        [TestMethod]
        public async Task DownloadFileAsync_SuccessfulDownload_ReturnsFilePath()
        {
            // Arrange
            var uri = "http://example.com/file.zip";
            var finalUri = "http://example.com/file.zip";
            var filePath = Path.Combine(Path.GetTempPath(), "file.zip");
            var contentStream = new MemoryStream();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            _mockStreamProvider.Setup(p => p.GetStreamAsync(uri, It.IsAny<CancellationToken>()))
                .ReturnsAsync((contentStream, 1024L, finalUri));

            _mockFileWriter.Setup(w => w.TryGetCachedFile(It.IsAny<string>(), out It.Ref<string>.IsAny)).Returns(false);

            _mockFileWriter.Setup(w => w.WriteAsync(contentStream, filePath, 1024L, finalUri, mockProgress.Object, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, false);

            // Assert
            Assert.AreEqual(filePath, result);
            _mockLogger.Verify(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Download completed")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Once);
        }

        [TestMethod]
        public async Task DownloadFileAsync_UsesCache_ReturnsCachedPath()
        {
            // Arrange
            var uri = "http://example.com/file.zip";
            var filePath = Path.Combine(Path.GetTempPath(), "file.zip");
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();
            string cachedPath = filePath;

            _mockStreamProvider.Setup(p => p.GetStreamAsync(uri, It.IsAny<CancellationToken>()))
                .ReturnsAsync((new MemoryStream(), 1024L, uri));

            _mockFileWriter.Setup(w => w.TryGetCachedFile(It.IsAny<string>(), out cachedPath)).Returns(true);

            // Act
            var result = await _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, true);

            // Assert
            Assert.AreEqual(filePath, result);
            _mockFileWriter.Verify(w => w.WriteAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task DownloadFileAsync_RetriesOnFailure_SucceedsOnRetry()
        {
            // Arrange
            var uri = "http://example.com/file.zip";
            var finalUri = "http://example.com/file.zip";
            var filePath = Path.Combine(Path.GetTempPath(), "file.zip");
            var contentStream = new MemoryStream();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            _mockStreamProvider.SetupSequence(p => p.GetStreamAsync(uri, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("First failure"))
                .ReturnsAsync((contentStream, 1024L, finalUri));

            _mockFileWriter.Setup(w => w.TryGetCachedFile(It.IsAny<string>(), out It.Ref<string>.IsAny)).Returns(false);

            _mockFileWriter.Setup(w => w.WriteAsync(contentStream, filePath, 1024L, finalUri, mockProgress.Object, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, false);

            // Assert
            Assert.AreEqual(filePath, result);
            _mockStreamProvider.Verify(p => p.GetStreamAsync(uri, It.IsAny<CancellationToken>()), Times.Exactly(2)); // One failure, one success
        }

        [TestMethod]
        [ExpectedException(typeof(Exception))]
        public async Task DownloadFileAsync_MaxRetriesExceeded_ThrowsException()
        {
            // Arrange
            var uri = "http://example.com/file.zip";
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            _mockStreamProvider.Setup(p => p.GetStreamAsync(uri, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("Persistent failure"));

            // Act
            await _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, false);
        }

        [TestMethod]
        [ExpectedException(typeof(OperationCanceledException))]
        public async Task DownloadFileAsync_CancellationRequested_ThrowsAndCleansUp()
        {
            // Arrange
            var uri = "http://example.com/file.zip";
            var finalUri = "http://example.com/file.zip";
            var filePath = Path.Combine(Path.GetTempPath(), "file.zip");
            var cts = new CancellationTokenSource();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            _mockStreamProvider.Setup(p => p.GetStreamAsync(uri, It.IsAny<CancellationToken>()))
                .ReturnsAsync((new MemoryStream(), 1024L, finalUri));

            _mockFileWriter.Setup(w => w.TryGetCachedFile(It.IsAny<string>(), out It.Ref<string>.IsAny)).Returns(false);

            _mockFileWriter.Setup(w => w.WriteAsync(It.IsAny<Stream>(), filePath, 1024L, finalUri, mockProgress.Object, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

            // Act
            await _downloader.DownloadFileAsync(uri, cts.Token, mockProgress.Object, false);
        }

        [TestMethod]
        public async Task DownloadFileAsync_InvalidFileNameFromUri_HandlesSanitization()
        {
            // Arrange (test the sanitization suggested)
            var uri = "http://example.com/file:with<invalid*chars.zip";
            var finalUri = uri;
            var expectedFilePath = Path.Combine(Path.GetTempPath(), "file_with_invalid_chars.zip");
            var contentStream = new MemoryStream();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            _mockStreamProvider.Setup(p => p.GetStreamAsync(uri, It.IsAny<CancellationToken>()))
                .ReturnsAsync((contentStream, 1024L, finalUri));

            _mockFileWriter.Setup(w => w.TryGetCachedFile(It.IsAny<string>(), out It.Ref<string>.IsAny)).Returns(false);

            _mockFileWriter.Setup(w => w.WriteAsync(contentStream, It.Is<string>(path => path.Contains("file_with_invalid_chars")), 1024L, finalUri, mockProgress.Object, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, false);

            // Assert
            Assert.AreEqual(expectedFilePath, result);
        }
    }
}