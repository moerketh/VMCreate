using Microsoft.Extensions.Logging;
using Moq;

namespace VMCreate.Tests
{
    [TestClass]
    public class FileStreamProviderTests
    {
        private Mock<ILogger<FileStreamProvider>> _mockLogger;
        private FileStreamProvider _provider;

        [TestInitialize]
        public void Setup()
        {
            _mockLogger = new Mock<ILogger<FileStreamProvider>>();
            _provider = new FileStreamProvider(_mockLogger.Object);
        }

        [TestMethod]
        public async Task GetWriteStreamAsync_FileExistsAndUseCache_ReturnsCachedTrueNoStream()
        {
            // Arrange
            var filePath = Path.Combine(Path.GetTempPath(), "testfile.zip");
            File.WriteAllBytes(filePath, new byte[0]); // Create temp file
            try
            {
                // Act
                var (writeStream, isCached) = await _provider.GetWriteStreamAsync(filePath, true);

                // Assert
                Assert.IsTrue(isCached);
                Assert.IsNull(writeStream);
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
        public async Task GetWriteStreamAsync_FileDoesNotExist_ReturnsStreamAndCachedFalse()
        {
            // Arrange
            var filePath = Path.Combine(Path.GetTempPath(), "nonexistent.zip");

            // Act
            var (writeStream, isCached) = await _provider.GetWriteStreamAsync(filePath, true);

            // Assert
            Assert.IsFalse(isCached);
            Assert.IsNotNull(writeStream);
            writeStream.Dispose(); // Clean up
            if (File.Exists(filePath)) File.Delete(filePath);
        }

        [TestMethod]
        public async Task GetWriteStreamAsync_NoCache_FileExists_ReturnsStreamAndCachedFalse()
        {
            // Arrange
            var filePath = Path.Combine(Path.GetTempPath(), "testfile.zip");
            File.WriteAllBytes(filePath, new byte[0]); // Create temp file
            try
            {
                // Act
                var (writeStream, isCached) = await _provider.GetWriteStreamAsync(filePath, false);

                // Assert
                Assert.IsFalse(isCached);
                Assert.IsNotNull(writeStream);
                writeStream.Dispose();
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
