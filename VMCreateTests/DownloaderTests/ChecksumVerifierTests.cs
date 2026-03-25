using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace VMCreate.Tests
{
    [TestClass]
    public class ChecksumVerifierTests
    {
        #region ParseChecksum Tests

        [TestMethod]
        public void ParseChecksum_GnuFormat_MatchesFilename()
        {
            var content = "abc123def456  debian-12.5.0-amd64-netinst.iso\ndef789abc012  debian-12.5.0-amd64-DVD-1.iso\n";
            var result = ChecksumVerifier.ParseChecksum(content, "debian-12.5.0-amd64-netinst.iso");
            Assert.AreEqual("abc123def456", result);
        }

        [TestMethod]
        public void ParseChecksum_GnuFormatWithStar_MatchesFilename()
        {
            var content = "abc123def456 *linuxmint-22.1-cinnamon-64bit.iso\n";
            var result = ChecksumVerifier.ParseChecksum(content, "linuxmint-22.1-cinnamon-64bit.iso");
            Assert.AreEqual("abc123def456", result);
        }

        [TestMethod]
        public void ParseChecksum_GnuFormat_NoMatchReturnsNull()
        {
            var content = "abc123def456  other-file.iso\n";
            var result = ChecksumVerifier.ParseChecksum(content, "my-file.iso");
            Assert.IsNull(result);
        }

        [TestMethod]
        public void ParseChecksum_BsdFormat_MatchesFilename()
        {
            var content = "# Rocky-9.3-x86_64-minimal.iso: 2046820352 bytes\nSHA256 (Rocky-9.3-x86_64-minimal.iso) = abc123def456\n";
            var result = ChecksumVerifier.ParseChecksum(content, "Rocky-9.3-x86_64-minimal.iso");
            Assert.AreEqual("abc123def456", result);
        }

        [TestMethod]
        public void ParseChecksum_BsdFormat_NoMatchReturnsNull()
        {
            var content = "SHA256 (other-file.iso) = abc123def456\n";
            var result = ChecksumVerifier.ParseChecksum(content, "my-file.iso");
            Assert.IsNull(result);
        }

        [TestMethod]
        public void ParseChecksum_BareHash_SingleLine()
        {
            var content = "abc123def456789012345678901234567890123456789012345678901234abcd\n";
            var result = ChecksumVerifier.ParseChecksum(content, "anything.iso");
            Assert.AreEqual("abc123def456789012345678901234567890123456789012345678901234abcd", result);
        }

        [TestMethod]
        public void ParseChecksum_BareHash_MultipleLinesReturnsNull()
        {
            // Two bare hashes without filenames — ambiguous, should return null
            var content = "abc123def456789012345678901234567890123456789012345678901234abcd\ndef789abc0123456789012345678901234567890123456789012345678901234\n";
            var result = ChecksumVerifier.ParseChecksum(content, "anything.iso");
            Assert.IsNull(result);
        }

        [TestMethod]
        public void ParseChecksum_EmptyContent_ReturnsNull()
        {
            Assert.IsNull(ChecksumVerifier.ParseChecksum("", "file.iso"));
            Assert.IsNull(ChecksumVerifier.ParseChecksum(null, "file.iso"));
            Assert.IsNull(ChecksumVerifier.ParseChecksum("  \n  \n", "file.iso"));
        }

        [TestMethod]
        public void ParseChecksum_CommentsIgnored()
        {
            var content = "# This is a comment\nabc123def456  my-file.iso\n# Another comment\n";
            var result = ChecksumVerifier.ParseChecksum(content, "my-file.iso");
            Assert.AreEqual("abc123def456", result);
        }

        [TestMethod]
        public void ParseChecksum_CaseInsensitiveFilename()
        {
            var content = "abc123def456  MyFile.ISO\n";
            var result = ChecksumVerifier.ParseChecksum(content, "myfile.iso");
            Assert.AreEqual("abc123def456", result);
        }

        [TestMethod]
        public void ParseChecksum_MixedFormats_MatchesCorrectLine()
        {
            var content = string.Join("\n",
                "# Checksums for Fedora-42",
                "SHA256 (Fedora-Workstation-Live-42-1.1.x86_64.iso) = aaa111",
                "SHA256 (Fedora-Silverblue-ostree-x86_64-42-1.1.iso) = bbb222",
                "");
            var result = ChecksumVerifier.ParseChecksum(content, "Fedora-Silverblue-ostree-x86_64-42-1.1.iso");
            Assert.AreEqual("bbb222", result);
        }

        #endregion

        #region VerifyAsync Integration Tests

        [TestMethod]
        public async Task VerifyAsync_MatchingChecksum_Passes()
        {
            // Create a temp file with known content
            var tempFile = Path.GetTempFileName();
            try
            {
                var content = Encoding.UTF8.GetBytes("Hello checksum verification!");
                await File.WriteAllBytesAsync(tempFile, content);

                var expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                var fileName = Path.GetFileName(tempFile);
                var checksumFileContent = $"{expectedHash}  {fileName}\n";

                var handler = new MockChecksumHttpHandler(checksumFileContent);
                var factory = new MockHttpClientFactory(handler);
                var logger = new Mock<ILogger<ChecksumVerifier>>();
                var verifier = new ChecksumVerifier(factory, logger.Object);

                await verifier.VerifyAsync(tempFile, "http://example.com/SHA256SUMS", "sha256",
                    CancellationToken.None, null);

                // If we get here, verification passed
                Assert.IsTrue(File.Exists(tempFile), "File should still exist after successful verification");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [TestMethod]
        public async Task VerifyAsync_MismatchedChecksum_ThrowsAndDeletesFile()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(tempFile, "Some content");
                var fileName = Path.GetFileName(tempFile);
                var checksumFileContent = $"0000000000000000000000000000000000000000000000000000000000000000  {fileName}\n";

                var handler = new MockChecksumHttpHandler(checksumFileContent);
                var factory = new MockHttpClientFactory(handler);
                var logger = new Mock<ILogger<ChecksumVerifier>>();
                var verifier = new ChecksumVerifier(factory, logger.Object);

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    verifier.VerifyAsync(tempFile, "http://example.com/SHA256SUMS", "sha256",
                        CancellationToken.None, null));

                Assert.IsTrue(ex.Message.Contains("Checksum verification failed"));
                Assert.IsFalse(File.Exists(tempFile), "File should be deleted after failed verification");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [TestMethod]
        public async Task VerifyAsync_NoMatchingFilename_Throws()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(tempFile, "Some content");
                var checksumFileContent = "abc123  some-other-file.iso\n";

                var handler = new MockChecksumHttpHandler(checksumFileContent);
                var factory = new MockHttpClientFactory(handler);
                var logger = new Mock<ILogger<ChecksumVerifier>>();
                var verifier = new ChecksumVerifier(factory, logger.Object);

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    verifier.VerifyAsync(tempFile, "http://example.com/SHA256SUMS", "sha256",
                        CancellationToken.None, null));

                Assert.IsTrue(ex.Message.Contains("Could not find checksum"));
                Assert.IsTrue(File.Exists(tempFile), "File should NOT be deleted when checksum not found");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [TestMethod]
        public async Task VerifyAsync_DefaultsToSha256_WhenAlgorithmNull()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                var content = Encoding.UTF8.GetBytes("Test SHA256 default");
                await File.WriteAllBytesAsync(tempFile, content);

                var expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                var fileName = Path.GetFileName(tempFile);
                var checksumFileContent = $"{expectedHash}  {fileName}\n";

                var handler = new MockChecksumHttpHandler(checksumFileContent);
                var factory = new MockHttpClientFactory(handler);
                var logger = new Mock<ILogger<ChecksumVerifier>>();
                var verifier = new ChecksumVerifier(factory, logger.Object);

                // Pass null algorithm — should default to sha256
                await verifier.VerifyAsync(tempFile, "http://example.com/SHA256SUMS", null,
                    CancellationToken.None, null);

                Assert.IsTrue(File.Exists(tempFile));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        #endregion

        #region VerifyInlineAsync Tests

        [TestMethod]
        public async Task VerifyInlineAsync_MatchingHash_Passes()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                var content = Encoding.UTF8.GetBytes("Inline checksum test");
                await File.WriteAllBytesAsync(tempFile, content);

                var expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

                var handler = new MockChecksumHttpHandler("");
                var factory = new MockHttpClientFactory(handler);
                var logger = new Mock<ILogger<ChecksumVerifier>>();
                var verifier = new ChecksumVerifier(factory, logger.Object);

                await verifier.VerifyInlineAsync(tempFile, expectedHash, "sha256",
                    CancellationToken.None, null);

                Assert.IsTrue(File.Exists(tempFile), "File should still exist after successful verification");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [TestMethod]
        public async Task VerifyInlineAsync_MismatchedHash_ThrowsAndDeletesFile()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(tempFile, "Some content");

                var handler = new MockChecksumHttpHandler("");
                var factory = new MockHttpClientFactory(handler);
                var logger = new Mock<ILogger<ChecksumVerifier>>();
                var verifier = new ChecksumVerifier(factory, logger.Object);

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    verifier.VerifyInlineAsync(tempFile,
                        "0000000000000000000000000000000000000000000000000000000000000000",
                        "sha256", CancellationToken.None, null));

                Assert.IsTrue(ex.Message.Contains("Checksum verification failed"));
                Assert.IsFalse(File.Exists(tempFile), "File should be deleted after failed verification");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [TestMethod]
        public async Task VerifyInlineAsync_CaseInsensitiveHash_Passes()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                var content = Encoding.UTF8.GetBytes("Case insensitive hash test");
                await File.WriteAllBytesAsync(tempFile, content);

                // Use UPPER case hash
                var expectedHash = Convert.ToHexString(SHA256.HashData(content));

                var handler = new MockChecksumHttpHandler("");
                var factory = new MockHttpClientFactory(handler);
                var logger = new Mock<ILogger<ChecksumVerifier>>();
                var verifier = new ChecksumVerifier(factory, logger.Object);

                await verifier.VerifyInlineAsync(tempFile, expectedHash, "SHA256",
                    CancellationToken.None, null);

                Assert.IsTrue(File.Exists(tempFile));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [TestMethod]
        public async Task VerifyInlineAsync_Sha512_Passes()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                var content = Encoding.UTF8.GetBytes("SHA512 inline test");
                await File.WriteAllBytesAsync(tempFile, content);

                var expectedHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA512.HashData(content)).ToLowerInvariant();

                var handler = new MockChecksumHttpHandler("");
                var factory = new MockHttpClientFactory(handler);
                var logger = new Mock<ILogger<ChecksumVerifier>>();
                var verifier = new ChecksumVerifier(factory, logger.Object);

                await verifier.VerifyInlineAsync(tempFile, expectedHash, "sha512",
                    CancellationToken.None, null);

                Assert.IsTrue(File.Exists(tempFile));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        #endregion

        #region Test Helpers

        private class MockChecksumHttpHandler : HttpMessageHandler
        {
            private readonly string _responseContent;

            public MockChecksumHttpHandler(string responseContent)
            {
                _responseContent = responseContent;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_responseContent)
                });
            }
        }

        private class MockHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;

            public MockHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

            public HttpClient CreateClient(string name) => new HttpClient(_handler);
        }

        #endregion
    }
}
