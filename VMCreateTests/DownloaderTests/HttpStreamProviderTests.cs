using Moq;
using Moq.Protected;
using System.Net;
using Microsoft.Extensions.Logging;
using VMCreate;

namespace VMCreateTests
{
    [TestClass]
    public class HttpStreamProviderTests
    {
        private Mock<IHttpClientFactory> _mockClientFactory;
        private Mock<ILogger<HttpStreamProvider>> _mockLogger;
        private HttpStreamProvider _provider;

        [TestInitialize]
        public void Setup()
        {
            _mockClientFactory = new Mock<IHttpClientFactory>();
            _mockLogger = new Mock<ILogger<HttpStreamProvider>>();
            _provider = new HttpStreamProvider(_mockClientFactory.Object, _mockLogger.Object);
        }

        [TestMethod]
        public async Task GetStreamAsync_SuccessfulRequest_ReturnsStreamAndDetails()
        {
            // Arrange
            var uri = "http://example.com/file.zip";
            var finalUri = "http://redirected.com/file.zip";
            var contentLength = 1024L;
            var contentStream = new MemoryStream();

            var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[0]) { Headers = { ContentLength = contentLength } }
            };
            mockResponse.RequestMessage = new HttpRequestMessage { RequestUri = new Uri(finalUri) };

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(mockResponse);

            var mockClient = new HttpClient(mockHandler.Object);
            mockClient.DefaultRequestHeaders.Add("User-Agent", "VMCreate");

            _mockClientFactory.Setup(f => f.CreateClient(string.Empty)).Returns(mockClient);

            // Act
            var (stream, length, returnedFinalUri) = await _provider.GetStreamAsync(uri, CancellationToken.None);

            // Assert
            Assert.AreEqual(contentLength, length);
            Assert.AreEqual(finalUri, returnedFinalUri);
            _mockLogger.Verify(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Final URI after redirects")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Once);
        }

        [TestMethod]
        [ExpectedException(typeof(HttpRequestException))]
        public async Task GetStreamAsync_FailedRequest_ThrowsException()
        {
            // Arrange
            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

            var mockClient = new HttpClient(mockHandler.Object);
            _mockClientFactory.Setup(f => f.CreateClient(string.Empty)).Returns(mockClient);

            // Act
            await _provider.GetStreamAsync("http://example.com", CancellationToken.None);
        }
    }
}
