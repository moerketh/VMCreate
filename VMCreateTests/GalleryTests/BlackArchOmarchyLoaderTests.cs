using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;
using VMCreate.Gallery;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Tests for <see cref="BlackArchOmarchy"/> — the Omarchy (Arch + Hyprland)
    /// base with BlackArch repos layered post-boot. The GitHub releases/latest
    /// response is mocked; ISO/checksum URLs are only constructed, never fetched.
    /// </summary>
    [TestClass]
    public sealed class BlackArchOmarchyLoaderTests
    {
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        private static IHttpClientFactory FactoryFor(System.Func<HttpRequestMessage, HttpResponseMessage> send)
        {
            var handler = new FuncHandler(send);
            var mock = new Mock<IHttpClientFactory>();
            mock.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(handler, disposeHandler: false));
            return mock.Object;
        }

        private sealed class FuncHandler : HttpMessageHandler
        {
            private readonly System.Func<HttpRequestMessage, HttpResponseMessage> _send;
            public FuncHandler(System.Func<HttpRequestMessage, HttpResponseMessage> send) => _send = send;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = _send(request);
                response.RequestMessage ??= request;
                return Task.FromResult(response);
            }
        }

        [TestMethod]
        public async Task LoadGalleryItems_ProducesCidataReadyOmarchyItemWithBothTags()
        {
            var factory = FactoryFor(_ => Json(
                """{"tag_name":"v4.0.4","published_at":"2026-09-15T21:39:29Z","assets":[]}"""));

            var items = await new BlackArchOmarchy(factory).LoadGalleryItems();

            Assert.AreEqual(1, items.Count);
            var item = items[0];

            Assert.AreEqual("BlackArch on Omarchy 4.0.4", item.Name);
            Assert.AreEqual("Security", item.Category);
            Assert.AreEqual(LinuxDistro.Arch, item.LinuxDistro);
            Assert.IsFalse(item.IsWindows);

            // ISO + GNU sha256 sidecar derived from the release tag (Omarchy mirror).
            Assert.AreEqual("https://iso.omarchy.org/omarchy-4.0.4.iso", item.DiskUri);
            Assert.AreEqual("https://iso.omarchy.org/omarchy-4.0.4.iso.sha256", item.ChecksumUri);
            Assert.AreEqual("sha256", item.ChecksumAlgorithm);
            Assert.AreEqual("ISO", item.FileType);
            Assert.IsFalse(item.NeedsExtraction);

            // Credentials consumed by the cidata seed builder at deploy time.
            Assert.AreEqual("omarchy", item.InitialUsername);
            Assert.AreEqual("omarchy", item.InitialPassword);
            Assert.AreEqual("false", item.SecureBoot);
            Assert.AreEqual("4.0.4", item.Version);
            Assert.IsTrue(item.HasUnattendedInstallPath);

            // Composed tags: "omarchy" gates sudo/SSH, "blackarch" gates BlackArchInstallStep.
            Assert.IsTrue(item.HasTag("omarchy"));
            Assert.IsTrue(item.HasTag("blackarch"));
        }

        [TestMethod]
        public async Task LoadGalleryItems_StripsLeadingVFromTag()
        {
            var factory = FactoryFor(_ => Json("""{"tag_name":"v5.1.0","published_at":"2027-01-01T00:00:00Z","assets":[]}"""));

            var items = await new BlackArchOmarchy(factory).LoadGalleryItems();

            Assert.AreEqual("https://iso.omarchy.org/omarchy-5.1.0.iso", items[0].DiskUri);
            Assert.AreEqual("5.1.0", items[0].Version);
        }

        [TestMethod]
        public async Task LoadGalleryItems_MissingTagName_Throws()
        {
            var factory = FactoryFor(_ => Json("""{"message":"Not Found"}"""));

            await Assert.ThrowsAsync<System.Exception>(() => new BlackArchOmarchy(factory).LoadGalleryItems());
        }

        [TestMethod]
        public async Task LoadGalleryItems_ServerError_Throws()
        {
            var factory = FactoryFor(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom")
            });

            await Assert.ThrowsAsync<HttpRequestException>(() => new BlackArchOmarchy(factory).LoadGalleryItems());
        }

        [TestMethod]
        public async Task LoadGalleryItems_DesktopSwitchNotOffered_KeepsHyprland()
        {
            // The composed {omarchy, blackarch} item must NOT surface the
            // BlackArch desktop catalog — switching to KDE/GNOME would replace
            // Hyprland, the whole point of the Omarchy base.
            var factory = FactoryFor(_ => Json("""{"tag_name":"v4.0.4","assets":[]}"""));
            var item = (await new BlackArchOmarchy(factory).LoadGalleryItems())[0];

            var catalog = new BlackArchDesktopCatalog();
            Assert.IsFalse(catalog.IsVisibleFor(item),
                "BlackArch-on-Omarchy keeps Hyprland — the desktop-switch catalog must not apply");
        }
    }
}
