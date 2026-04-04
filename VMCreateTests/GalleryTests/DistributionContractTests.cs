using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using VMCreate;
using VMCreate.Gallery;
using VMCreate.Gallery.distributions;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Generic contract tests applied to every distribution loader.
    /// Static loaders are tested directly; HTTP loaders receive mocked HTML responses.
    /// These tests are fast (no network I/O) and safe for CI.
    /// </summary>
    [TestClass]
    public sealed class DistributionContractTests
    {
        #region Contract Invariant Helper

        private static void AssertContractInvariants(List<GalleryItem> items, string loaderName)
        {
            Assert.IsNotNull(items, $"{loaderName}: returned null");
            Assert.IsTrue(items.Count >= 1, $"{loaderName}: returned 0 items");

            foreach (var item in items)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(item.Name),
                    $"{loaderName}: Name is empty on an item");
                Assert.IsFalse(string.IsNullOrWhiteSpace(item.DiskUri),
                    $"{loaderName}: DiskUri is empty on '{item.Name}'");
                Assert.IsTrue(Uri.IsWellFormedUriString(item.DiskUri, UriKind.Absolute),
                    $"{loaderName}: DiskUri is not a valid absolute URI on '{item.Name}': {item.DiskUri}");
                Assert.IsFalse(string.IsNullOrWhiteSpace(item.Publisher),
                    $"{loaderName}: Publisher is empty on '{item.Name}'");
                Assert.IsFalse(string.IsNullOrWhiteSpace(item.Description),
                    $"{loaderName}: Description is empty on '{item.Name}'");
                Assert.AreNotEqual("Unknown", item.FileType,
                    $"{loaderName}: FileType is Unknown on '{item.Name}' (DiskUri: {item.DiskUri})");
            }
        }

        #endregion

        #region Mock HTTP Factory

        private sealed class FuncHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;

            public FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> send) => _send = send;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = _send(request);
                response.RequestMessage ??= request;
                return Task.FromResult(response);
            }
        }

        private static IHttpClientFactory FactoryFor(string content)
        {
            var handler = new FuncHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content)
                });
            var mock = new Mock<IHttpClientFactory>();
            // disposeHandler: false so the shared handler survives multiple CreateClient() calls (e.g. Ubuntu)
            mock.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(handler, disposeHandler: false));
            return mock.Object;
        }

        private static IHttpClientFactory FactoryFor(
            Func<HttpRequestMessage, HttpResponseMessage> send)
        {
            var handler = new FuncHandler(send);
            var mock = new Mock<IHttpClientFactory>();
            mock.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(handler, disposeHandler: false));
            return mock.Object;
        }

        private static IHttpClientFactory ServerErrorFactory() =>
            FactoryFor(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("error")
            });

        private static IHttpClientFactory EmptyHtmlFactory() =>
            FactoryFor("<html><body>No downloads here</body></html>");

        #endregion

        // ════════════════════════════════════════════════════════════════════
        // STATIC LOADERS — data-driven over all five static loaders
        // ════════════════════════════════════════════════════════════════════

        public static IEnumerable<object[]> StaticLoaders => new object[][]
        {
            new object[] { new Arch(),                nameof(Arch) },
            new object[] { new NixOS(),               nameof(NixOS) },
            new object[] { new FedoraSilverblue(),    nameof(FedoraSilverblue) },
            new object[] { new PwnCloudOS(EmptyHtmlFactory()), nameof(PwnCloudOS) },
            new object[] { new FedoraSecurityLab(),   nameof(FedoraSecurityLab) },
            // Security loaders
            new object[] { new REMnux(),             nameof(REMnux) },
            new object[] { new CAINE(),              nameof(CAINE) },
            new object[] { new Whonix(),             nameof(Whonix) },
            new object[] { new Tsurugi(),            nameof(Tsurugi) },
            // General loaders
            new object[] { new FedoraWorkstation(),  nameof(FedoraWorkstation) },
            new object[] { new OpenSuseTumbleweed(), nameof(OpenSuseTumbleweed) },
            new object[] { new LinuxMint(),          nameof(LinuxMint) },
            new object[] { new AlpineLinux(),        nameof(AlpineLinux) },
            new object[] { new RockyLinux(),         nameof(RockyLinux) },
        };

        [TestMethod]
        [DynamicData(nameof(StaticLoaders))]
        public async Task StaticLoader_MeetsContract(IGalleryLoader loader, string name)
        {
            var items = await loader.LoadGalleryItems();
            AssertContractInvariants(items, name);
        }

        [TestMethod]
        [DynamicData(nameof(StaticLoaders))]
        public async Task StaticLoader_CancelledToken_DoesNotThrow(IGalleryLoader loader, string name)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            // Static loaders don't observe the token — they must return results immediately.
            var items = await loader.LoadGalleryItems(cts.Token);
            Assert.IsTrue(items.Count >= 1, $"{name}: should return items even with a cancelled token");
        }

        // ════════════════════════════════════════════════════════════════════
        // HTTP LOADERS — individual happy-path tests with canned HTML / JSON
        // ════════════════════════════════════════════════════════════════════

        [TestMethod]
        public async Task BlackArch_MeetsContract()
        {
            const string html =
                @"<a href=""https://ftp.example.com/blackarch-linux-slim-2023.05.01-x86_64.iso"">Slim</a>
                   <a href=""https://ftp.example.com/blackarch-linux-2023.04.01.ova"">OVA</a>";

            var items = await new BlackArch(FactoryFor(html)).LoadGalleryItems();

            AssertContractInvariants(items, nameof(BlackArch));
            Assert.AreEqual(2, items.Count);
        }

        [TestMethod]
        public async Task LoadKaliCurrent_MeetsContract()
        {
            const string html =
                @"<a href=""kali-linux-2024.3-hyperv-amd64.7z"">kali-linux-2024.3-hyperv-amd64.7z</a>
                   <td class=""size"">3.5G</td>
                   <td class=""date"">2024-09-11</td>";

            var items = await new Kali(FactoryFor(html)).LoadGalleryItems();

            AssertContractInvariants(items, nameof(Kali));
            Assert.AreEqual(1, items.Count);
        }

        [TestMethod]
        public async Task LoadParrot_MeetsContract()
        {
            const string html =
                @"<a href=""Parrot-security-6.1_amd64.iso"">Parrot-security-6.1_amd64.iso</a> 15-Jan-2024 10:30  1234567890
                  <a href=""Parrot-home-6.1_amd64.iso"">Parrot-home-6.1_amd64.iso</a> 15-Jan-2024 10:30  1234567890";

            var items = await new Parrot(FactoryFor(html)).LoadGalleryItems();

            AssertContractInvariants(items, nameof(Parrot));
            Assert.AreEqual(2, items.Count);
        }

        [TestMethod]
        public async Task LoadPentooCurrent_MeetsContract()
        {
            const string json = """
                [
                    { "name": "Pentoo Full amd64 hardened", "type": "Daily",
                      "version": "2026.0_p20260301",
                      "path": "isos/daily-autobuilds/Pentoo_Full_amd64_hardened/" }
                ]
                """;

            var items = await new PentooCurrent(FactoryFor(json)).LoadGalleryItems();

            AssertContractInvariants(items, nameof(PentooCurrent));
            Assert.AreEqual(1, items.Count);
        }


        [TestMethod]
        public async Task Ubuntu_MeetsContract()
        {
            // All build-info.txt fetches receive the same canned response.
            var items = await new Ubuntu(
                new Mock<ILogger<Ubuntu>>().Object,
                FactoryFor("serial=20240301")).LoadGalleryItems();

            AssertContractInvariants(items, nameof(Ubuntu));
            Assert.AreEqual(2, items.Count);
        }

        [TestMethod]
        public async Task LoadTails_MeetsContract()
        {
            const string json = """
                {
                    "installations": [{
                        "version": "7.5",
                        "installation-paths": [
                            {
                                "type": "img",
                                "target-files": [{
                                    "url": "https://download.tails.net/tails/stable/tails-amd64-7.5/tails-amd64-7.5.img",
                                    "sha256": "abc123",
                                    "size": 2041577472
                                }]
                            },
                            {
                                "type": "iso",
                                "target-files": [{
                                    "url": "https://download.tails.net/tails/stable/tails-amd64-7.5/tails-amd64-7.5.iso",
                                    "sha256": "def456",
                                    "size": 2031644672
                                }]
                            }
                        ]
                    }]
                }
                """;

            var items = await new Tails(FactoryFor(json)).LoadGalleryItems();

            AssertContractInvariants(items, nameof(Tails));
            Assert.AreEqual(1, items.Count);
            StringAssert.EndsWith(items[0].DiskUri, ".img", StringComparison.OrdinalIgnoreCase);
        }

        [TestMethod]
        public async Task LoadSecurityOnion_MeetsContract()
        {
            const string json = """
                {
                    "tag_name": "2.4.110-20250101",
                    "assets": [{
                        "name": "securityonion-2.4.110-20250101.iso",
                        "browser_download_url": "https://github.com/Security-Onion-Solutions/securityonion/releases/download/2.4.110-20250101/securityonion-2.4.110-20250101.iso"
                    }]
                }
                """;

            var items = await new SecurityOnion(FactoryFor(json)).LoadGalleryItems();

            AssertContractInvariants(items, nameof(SecurityOnion));
            Assert.AreEqual(1, items.Count);
        }

        [TestMethod]
        public async Task Debian_MeetsContract()
        {
            const string html =
                @"<a href=""debian-12.10.0-amd64-netinst.iso"">debian-12.10.0-amd64-netinst.iso</a>  2024-02-10 09:46  667M";

            var items = await new Debian(FactoryFor(html)).LoadGalleryItems();

            AssertContractInvariants(items, nameof(Debian));
            Assert.AreEqual(1, items.Count);
        }

        // ════════════════════════════════════════════════════════════════════
        // GENERIC ERROR / NO-MATCH — data-driven over all HTTP loaders
        // ════════════════════════════════════════════════════════════════════

        public static IEnumerable<object[]> HttpLoaderFactories =>
        [
            ["BlackArch",          new Func<IHttpClientFactory, IGalleryLoader>(f => new BlackArch(f))],
            ["LoadKaliCurrent",    new Func<IHttpClientFactory, IGalleryLoader>(f => new Kali(f))],
            ["LoadParrot",         new Func<IHttpClientFactory, IGalleryLoader>(f => new Parrot(f))],
            ["LoadPentooCurrent",  new Func<IHttpClientFactory, IGalleryLoader>(f => new PentooCurrent(f))],
            ["LoadTails",          new Func<IHttpClientFactory, IGalleryLoader>(f => new Tails(f))],
            ["LoadSecurityOnion",  new Func<IHttpClientFactory, IGalleryLoader>(f => new SecurityOnion(f))],
            ["Debian",             new Func<IHttpClientFactory, IGalleryLoader>(f => new Debian(f))],
        ];

        [TestMethod]
        [DynamicData(nameof(HttpLoaderFactories))]
        public async Task HttpLoader_ServerError_Throws(
            string name, Func<IHttpClientFactory, IGalleryLoader> create)
        {
            await Assert.ThrowsAsync<HttpRequestException>(
                () => create(ServerErrorFactory()).LoadGalleryItems(),
                $"{name}: should throw HttpRequestException on HTTP 500");
        }

        [TestMethod]
        [DynamicData(nameof(HttpLoaderFactories))]
        public async Task HttpLoader_NoMatchInResponse_Throws(
            string name, Func<IHttpClientFactory, IGalleryLoader> create)
        {
            // Each loader must throw *some* exception when the response has no recognisable data.
            // Use try/catch (polymorphic) because exact types vary: loaders that now parse JSON
            // (Pentoo, Tails) throw JsonException while HTML-scraping loaders throw Exception.
            bool threw = false;
            try
            {
                await create(EmptyHtmlFactory()).LoadGalleryItems();
            }
            catch (Exception)
            {
                threw = true;
            }
            Assert.IsTrue(threw,
                $"{name}: should throw when the response contains no recognisable download data");
        }

        [TestMethod]
        [DynamicData(nameof(HttpLoaderFactories))]
        public async Task HttpLoader_CancelledToken_Throws(
            string name, Func<IHttpClientFactory, IGalleryLoader> create)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Use try/catch (polymorphic) because MSTest ThrowsAsync uses exact-type matching
            // and HttpClient wraps cancellation as TaskCanceledException : OperationCanceledException.
            OperationCanceledException caught = null;
            try
            {
                await create(FactoryFor("anything")).LoadGalleryItems(cts.Token);
            }
            catch (OperationCanceledException ex)
            {
                caught = ex;
            }

            Assert.IsNotNull(caught,
                $"{name}: expected OperationCanceledException (or TaskCanceledException) on a pre-cancelled token");
        }
    }
}
