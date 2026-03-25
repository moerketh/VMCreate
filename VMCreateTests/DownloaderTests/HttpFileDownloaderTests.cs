using Microsoft.Extensions.Logging;
using Moq;
using System.Net;

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
            var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[0]) { Headers = { ContentLength = 1024 } },
                RequestMessage = new HttpRequestMessage { RequestUri = new Uri(finalUri) }
            };
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            _mockStreamProvider.Setup(p => p.GetResponseAsync(uri, It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockResponse);

            _mockFileStreamProvider.Setup(p => p.GetWriteStreamAsync(It.IsAny<string>(), false))
                .ReturnsAsync((new MemoryStream(), false));

            _mockStreamCopier.Setup(c => c.CopyAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), 1024L, finalUri, mockProgress.Object, It.IsAny<CancellationToken>()))
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
            var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[0]) { Headers = { ContentLength = 1024 } },
                RequestMessage = new HttpRequestMessage { RequestUri = new Uri(finalUri) }
            };
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            _mockStreamProvider.Setup(p => p.GetResponseAsync(uri, It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockResponse);

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
            var mockResponse2 = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[0]) { Headers = { ContentLength = 1024 } },
                RequestMessage = new HttpRequestMessage { RequestUri = new Uri(finalUri) }
            };
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            // First call throws an HttpRequestException (the only exception the downloader retries on);
            // second call succeeds. Returning a 500 response directly would cause a NullReferenceException
            // because the mock response has no RequestMessage set.
            _mockStreamProvider.SetupSequence(p => p.GetResponseAsync(uri, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("Simulated server error on attempt 1"))
                .ReturnsAsync(mockResponse2);

            _mockFileStreamProvider.Setup(p => p.GetWriteStreamAsync(It.IsAny<string>(), false))
                .ReturnsAsync((new MemoryStream(), false));

            _mockStreamCopier.Setup(c => c.CopyAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), 1024L, finalUri, mockProgress.Object, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, false);

            // Assert
            Assert.IsTrue(result.EndsWith("file.zip"));
            _mockStreamProvider.Verify(p => p.GetResponseAsync(uri, It.IsAny<CancellationToken>()), Times.Exactly(2)); // One failure, one success
        }

        [TestMethod]
        public async Task DownloadFileAsync_MaxRetriesExceeded_ThrowsException()
        {
            // Arrange
            var uri = "http://example.com/file.zip";
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            _mockStreamProvider.Setup(p => p.GetResponseAsync(uri, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("Persistent failure"));

            // Act
            await Assert.ThrowsAsync<Exception>(
                () => _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, false));
        }

        [TestMethod]
        public async Task DownloadFileAsync_CancellationRequested_ThrowsAndCleansUp()
        {
            // Arrange
            var uri = "http://example.com/file.zip";
            var finalUri = "http://example.com/file.zip";
            var filePath = Path.Combine(Path.GetTempPath(), "file.zip");
            var cts = new CancellationTokenSource();
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();
            var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[0]) { Headers = { ContentLength = 1024 } },
                RequestMessage = new HttpRequestMessage { RequestUri = new Uri(finalUri) }
            };

            _mockStreamProvider.Setup(p => p.GetResponseAsync(uri, It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockResponse);

            _mockFileStreamProvider.Setup(p => p.GetWriteStreamAsync(It.IsAny<string>(), false))
                .ReturnsAsync((new MemoryStream(), false));

            _mockStreamCopier.Setup(c => c.CopyAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), 1024L, finalUri, mockProgress.Object, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

            // Act
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => _downloader.DownloadFileAsync(uri, cts.Token, mockProgress.Object, false));
        }

        [TestMethod]
        public async Task DownloadFileAsync_InvalidFileNameFromUri_HandlesSanitization()
        {
            // Arrange
            // Use percent-encoded pipe characters (%7C) in the URI path. The downloader derives the
            // filename from Uri.LocalPath which decodes %7C → '|', an invalid Windows filename char.
            // Those chars are then replaced with '_', yielding "file_with_invalid_chars.zip".
            var uri = "http://example.com/file%7Cwith%7Cinvalid%7Cchars.zip";
            var finalUri = uri;
            var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[0]) { Headers = { ContentLength = 1024 } },
                RequestMessage = new HttpRequestMessage { RequestUri = new Uri(finalUri) }
            };
            var mockProgress = new Mock<IProgress<CreateVMProgressInfo>>();

            _mockStreamProvider.Setup(p => p.GetResponseAsync(uri, It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockResponse);

            _mockFileStreamProvider.Setup(p => p.GetWriteStreamAsync(It.Is<string>(path => path.Contains("file_with_invalid_chars")), false))
                .ReturnsAsync((new MemoryStream(), false));

            _mockStreamCopier.Setup(c => c.CopyAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), It.IsAny<long?>(), It.IsAny<string>(), mockProgress.Object, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _downloader.DownloadFileAsync(uri, CancellationToken.None, mockProgress.Object, false);

            // Assert
            Assert.IsTrue(result.Contains("file_with_invalid_chars"),
                $"Expected path to contain 'file_with_invalid_chars' but was: {result}");
        }
    }
}