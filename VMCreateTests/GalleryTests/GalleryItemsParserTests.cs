using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Net;
using VMCreate.Gallery;

namespace VMCreate.Tests.GalleryTests
{
    [TestClass]
    public sealed class GalleryItemsParserTests
    {
        private Mock<ILogger<GalleryItemsParser>> _mockLogger;
        private Mock<IHttpClientFactory> _mockClientFactory;

        [TestInitialize]
        public void Setup()
        {
            _mockLogger = new Mock<ILogger<GalleryItemsParser>>();
            _mockClientFactory = new Mock<IHttpClientFactory>();
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private GalleryItemsParser MakeParser(string httpResponseBody = null)
        {
            if (httpResponseBody is not null)
            {
                var handler = new Mock<HttpMessageHandler>();
                handler.Protected()
                    .Setup<Task<HttpResponseMessage>>(
                        "SendAsync",
                        ItExpr.IsAny<HttpRequestMessage>(),
                        ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(httpResponseBody)
                    });

                var client = new HttpClient(handler.Object);
                _mockClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
            }
            else
            {
                _mockClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                    .Returns(new HttpClient());
            }

            return new GalleryItemsParser(_mockLogger.Object, _mockClientFactory.Object);
        }

        private static string MinimalJson(string name = "TestDistro", string diskUri = "https://example.com/disk.vhdx") =>
            $$"""
            {
                "images": [
                    {
                        "name": "{{name}}",
                        "disk": { "uri": "{{diskUri}}" }
                    }
                ]
            }
            """;

        // ── LoadJsonFromFile ──────────────────────────────────────────────────────

        [TestMethod]
        public void LoadJsonFromFile_ValidJson_ReturnsParsedItem()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, MinimalJson());

                var result = MakeParser().LoadJsonFromFile(path);

                Assert.AreEqual(1, result.Count);
                Assert.AreEqual("TestDistro", result[0].Name);
                Assert.AreEqual("https://example.com/disk.vhdx", result[0].DiskUri);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void LoadJsonFromFile_FileNotFound_ReturnsEmptyList()
        {
            var result = MakeParser().LoadJsonFromFile(@"C:\does\not\exist.json");

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void LoadJsonFromFile_InvalidJson_ReturnsEmptyList()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "not json at all {{{");

                var result = MakeParser().LoadJsonFromFile(path);

                Assert.AreEqual(0, result.Count);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void LoadJsonFromFile_EmptyImagesArray_ReturnsEmptyList()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, """{ "images": [] }""");

                var result = MakeParser().LoadJsonFromFile(path);

                Assert.AreEqual(0, result.Count);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void LoadJsonFromFile_MissingName_SkipsEntry()
        {
            var json = """
                {
                    "images": [
                        { "disk": { "uri": "https://example.com/d.vhdx" } }
                    ]
                }
                """;
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, json);

                var result = MakeParser().LoadJsonFromFile(path);

                Assert.AreEqual(0, result.Count);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void LoadJsonFromFile_MissingDiskUri_SkipsEntry()
        {
            var json = """
                {
                    "images": [
                        { "name": "NoDisk" }
                    ]
                }
                """;
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, json);

                var result = MakeParser().LoadJsonFromFile(path);

                Assert.AreEqual(0, result.Count);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void LoadJsonFromFile_DescriptionAsArray_IsJoined()
        {
            var json = """
                {
                    "images": [
                        {
                            "name": "ArrayDesc",
                            "disk": { "uri": "https://example.com/a.vhdx" },
                            "description": ["Line one.", "Line two."]
                        }
                    ]
                }
                """;
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, json);

                var result = MakeParser().LoadJsonFromFile(path);

                Assert.AreEqual(1, result.Count);
                Assert.AreEqual("Line one. Line two.", result[0].Description);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void LoadJsonFromFile_DescriptionAsString_IsPreserved()
        {
            var json = """
                {
                    "images": [
                        {
                            "name": "StringDesc",
                            "disk": { "uri": "https://example.com/s.vhdx" },
                            "description": "A plain string description."
                        }
                    ]
                }
                """;
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, json);

                var result = MakeParser().LoadJsonFromFile(path);

                Assert.AreEqual(1, result.Count);
                Assert.AreEqual("A plain string description.", result[0].Description);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void LoadJsonFromFile_AllOptionalFields_MappedCorrectly()
        {
            var json = """
                {
                    "images": [
                        {
                            "name": "Full",
                            "publisher": "Acme Corp",
                            "version": "2024.1",
                            "lastUpdated": "2024-01-01",
                            "description": "Desc",
                            "thumbnail": { "uri": "https://example.com/thumb.png" },
                            "logo":      { "uri": "https://example.com/logo.png"  },
                            "symbol":    { "uri": "https://example.com/sym.png"   },
                            "disk": {
                                "uri": "https://example.com/disk.vhdx"
                            },
                            "config": {
                                "secureBoot": "true",
                                "enhancedSessionTransportType": "HvSocket"
                            }
                        }
                    ]
                }
                """;
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, json);

                var result = MakeParser().LoadJsonFromFile(path);

                Assert.AreEqual(1, result.Count);
                var item = result[0];
                Assert.AreEqual("Full", item.Name);
                Assert.AreEqual("Acme Corp", item.Publisher);
                Assert.AreEqual("2024.1", item.Version);
                Assert.AreEqual("2024-01-01", item.LastUpdated);
                Assert.AreEqual("Desc", item.Description);
                Assert.AreEqual("https://example.com/thumb.png", item.ThumbnailUri);
                Assert.AreEqual("https://example.com/sym.png", item.SymbolUri);
                Assert.AreEqual("https://example.com/disk.vhdx", item.DiskUri);
                Assert.AreEqual("true", item.SecureBoot);
                Assert.AreEqual("HvSocket", item.EnhancedSessionTransportType);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void LoadJsonFromFile_MultipleImages_AllReturned()
        {
            var json = """
                {
                    "images": [
                        { "name": "Distro1", "disk": { "uri": "https://example.com/1.vhdx" } },
                        { "name": "Distro2", "disk": { "uri": "https://example.com/2.vhdx" } },
                        { "name": "Distro3", "disk": { "uri": "https://example.com/3.vhdx" } }
                    ]
                }
                """;
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, json);

                var result = MakeParser().LoadJsonFromFile(path);

                Assert.AreEqual(3, result.Count);
                CollectionAssert.AreEqual(
                    new[] { "Distro1", "Distro2", "Distro3" },
                    result.Select(i => i.Name).ToArray());
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void LoadJsonFromFile_MixedValidAndInvalidEntries_OnlyValidReturned()
        {
            var json = """
                {
                    "images": [
                        { "name": "Valid", "disk": { "uri": "https://example.com/v.vhdx" } },
                        { "disk": { "uri": "https://example.com/missing-name.vhdx" } },
                        { "name": "NoDisk" }
                    ]
                }
                """;
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, json);

                var result = MakeParser().LoadJsonFromFile(path);

                Assert.AreEqual(1, result.Count);
                Assert.AreEqual("Valid", result[0].Name);
            }
            finally { File.Delete(path); }
        }

        // ── LoadJsonFromFiles ─────────────────────────────────────────────────────

        [TestMethod]
        public void LoadJsonFromFiles_DirectoryWithMultipleJsonFiles_ReturnsAllItems()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "a.json"), MinimalJson("A", "https://example.com/a.vhdx"));
                File.WriteAllText(Path.Combine(dir, "b.json"), MinimalJson("B", "https://example.com/b.vhdx"));
                File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignored");

                var result = MakeParser().LoadJsonFromFiles(dir);

                Assert.AreEqual(2, result.Count);
                var names = result.Select(i => i.Name).OrderBy(n => n).ToList();
                CollectionAssert.AreEqual(new[] { "A", "B" }, names);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [TestMethod]
        public void LoadJsonFromFiles_DirectoryNotFound_ReturnsEmptyList()
        {
            var result = MakeParser().LoadJsonFromFiles(@"C:\nonexistent\dir");

            Assert.AreEqual(0, result.Count);
        }

        // ── LoadJsonFromUrl ───────────────────────────────────────────────────────

        [TestMethod]
        public async Task LoadJsonFromUrl_ValidResponse_ReturnsParsedItems()
        {
            var parser = MakeParser(MinimalJson("HttpDistro", "https://example.com/http.vhdx"));

            var result = await parser.LoadJsonFromUrl("https://example.com/gallery.json");

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("HttpDistro", result[0].Name);
        }

        [TestMethod]
        public async Task LoadJsonFromUrl_HttpFailure_ReturnsEmptyList()
        {
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Connection refused"));

            _mockClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient(handler.Object));

            var parser = new GalleryItemsParser(_mockLogger.Object, _mockClientFactory.Object);

            var result = await parser.LoadJsonFromUrl("https://example.com/gallery.json");

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public async Task LoadJsonFromUrl_CancellationRequested_PropagatesCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new TaskCanceledException());

            _mockClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient(handler.Object));

            var parser = new GalleryItemsParser(_mockLogger.Object, _mockClientFactory.Object);

            // OperationCanceledException (or its subclass TaskCanceledException) must propagate;
            // it must not be swallowed and returned as an empty list.
            try
            {
                await parser.LoadJsonFromUrl("https://example.com/g.json", cts.Token);
                Assert.Fail("Expected OperationCanceledException was not thrown.");
            }
            catch (OperationCanceledException)
            {
                // Correct — test passes.
            }
        }
    }

    /// <summary>
    /// Tests for <see cref="GalleryItem.FileType"/> property derivation from DiskUri.
    /// </summary>
    [TestClass]
    public sealed class GalleryItemFileTypeTests
    {
        [TestMethod]
        [DataRow("https://example.com/disk.vmdk", "VMDK")]
        [DataRow("https://example.com/disk.vmdk.xz", "VMDK")]
        [DataRow("https://example.com/disk.vmdk.gz", "VMDK")]
        [DataRow("https://example.com/disk.qcow2", "QCOW2")]
        [DataRow("https://example.com/disk.vhdx", "VHDX")]
        [DataRow("https://example.com/disk.vhd", "VHD")]
        [DataRow("https://example.com/install.iso", "ISO")]
        [DataRow("https://example.com/image.ova", "OVA")]
        [DataRow("https://example.com/image.zip", "Archive")]
        [DataRow("https://example.com/image.7z", "Archive")]
        [DataRow("https://example.com/image.tar", "Archive")]
        [DataRow("https://example.com/readme.txt", "Other")]
        public void FileType_DerivesFromDiskUri(string diskUri, string expected)
        {
            var item = new GalleryItem { DiskUri = diskUri };
            Assert.AreEqual(expected, item.FileType);
        }

        [TestMethod]
        public void FileType_NullDiskUri_ReturnsUnknown()
        {
            var item = new GalleryItem { DiskUri = null };
            Assert.AreEqual("Unknown", item.FileType);
        }

        [TestMethod]
        public void FileType_EmptyDiskUri_ReturnsUnknown()
        {
            var item = new GalleryItem { DiskUri = "" };
            Assert.AreEqual("Unknown", item.FileType);
        }


    }
}
