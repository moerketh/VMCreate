using Microsoft.Extensions.Logging;
using Moq;

namespace VMCreate.Tests
{
    [TestClass]
    public class HttpFileDownloaderTests
    {
        private Mock<ILogger<HttpFileDownloader>> _mockLogger;
        private Mock<IHttpStreamProvider> _mockStreamProvider;
        private Mock<IFileStreamProvider> _mockFileStreamProvider;
        private Mock<StreamCopierWithProgress> _mockStreamCopier;
        private HttpFileDownloader _downloader;

        [TestInitialize]
        public void Setup()
        {
            _mockLogger = new Mock<ILogger<HttpFileDownloader>>();
            _mockStreamProvider = new Mock<IHttpStreamProvider>();
            _mockFileStreamProvider = new Mock<IFileStreamProvider>();
            _mockStreamCopier = new Mock<StreamCopierWithProgress>();
            _downloader = new HttpFileDownloader(_mockLogger.Object, _mockStreamProvider.Object, _mockFileStreamProvider.Object, _mockStreamCopier.Object);
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

            _mockFileStreamProvider.Setup(p => p.GetWriteStreamAsync(It.IsAny<string>(), false))
                .ReturnsAsync((new MemoryStream(), false));

            _mockStreamCopier.Setup(c => c.CopyAsync(contentStream, It.IsAny<Stream>(), 1024L, finalUri, mockProgress.Object, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, false);

            // Assert
            Assert.IsTrue(result.EndsWith("file.zip"));
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
            var finalUri = uri;
            var filePath = Path.Combine(Path.GetTempPath(), "file.zip");
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            _mockStreamProvider.Setup(p => p.GetStreamAsync(uri, It.IsAny<CancellationToken>()))
                .ReturnsAsync((new MemoryStream(), 1024L, uri));

            _mockFileStreamProvider.Setup(p => p.GetWriteStreamAsync(It.IsAny<string>(), true))
                .ReturnsAsync((null, true));

            // Act
            var result = await _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, true);

            // Assert
            Assert.IsTrue(result.EndsWith("file.zip"));
            _mockStreamCopier.Verify(c => c.CopyAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Never);
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

            _mockFileStreamProvider.Setup(p => p.GetWriteStreamAsync(It.IsAny<string>(), false))
                .ReturnsAsync((new MemoryStream(), false));

            _mockStreamCopier.Setup(c => c.CopyAsync(contentStream, It.IsAny<Stream>(), 1024L, finalUri, mockProgress.Object, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, false);

            // Assert
            Assert.IsTrue(result.EndsWith("file.zip"));
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

            _mockFileStreamProvider.Setup(p => p.GetWriteStreamAsync(It.IsAny<string>(), false))
                .ReturnsAsync((new MemoryStream(), false));

            _mockStreamCopier.Setup(c => c.CopyAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), 1024L, finalUri, mockProgress.Object, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

            // Act
            await _downloader.DownloadFileAsync(uri, cts.Token, mockProgress.Object, false);
        }

        [TestMethod]
        public async Task DownloadFileAsync_InvalidFileNameFromUri_HandlesSanitization()
        {
            // Arrange
            var uri = "http://example.com/file:with<invalid*chars.zip";
            var finalUri = uri;
            var contentStream = new MemoryStream();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            _mockStreamProvider.Setup(p => p.GetStreamAsync(uri, It.IsAny<CancellationToken>()))
                .ReturnsAsync((contentStream, 1024L, finalUri));

            _mockFileStreamProvider.Setup(p => p.GetWriteStreamAsync(It.Is<string>(path => path.Contains("file_with_invalid_chars")), false))
                .ReturnsAsync((new MemoryStream(), false));

            _mockStreamCopier.Setup(c => c.CopyAsync(contentStream, It.IsAny<Stream>(), 1024L, finalUri, mockProgress.Object, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, false);

            // Assert
            Assert.IsTrue(result.Contains("file_with_invalid_chars"));
        }
    }
}