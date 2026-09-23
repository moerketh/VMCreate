using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;
using VMCreate.Gallery;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Tests for the <see cref="Omarchy"/> gallery loader. The GitHub
    /// releases/latest response is mocked; ISO/checksum URLs are never fetched
    /// by the loader itself (only constructed), so these tests need no network.
    /// </summary>
    [TestClass]
    public sealed class OmarchyLoaderTests
    {
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };

        private static IHttpClientFactory FactoryFor(Func<HttpRequestMessage, HttpResponseMessage> send)
        {
            var handler = new FuncHandler(send);
            var mock = new Mock<IHttpClientFactory>();
            mock.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(handler, disposeHandler: false));
            return mock.Object;
        }

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

        [TestMethod]
        public async Task LoadGalleryItems_LatestRelease_ProducesCidataReadyItem()
        {
            var factory = FactoryFor(_ => Json(
                """
                {"tag_name":"v4.0.4","published_at":"2026-09-15T21:39:29Z","assets":[]}
                """));

            var items = await new Omarchy(factory).LoadGalleryItems();

            Assert.AreEqual(1, items.Count);
            var item = items[0];

            Assert.AreEqual("Omarchy 4.0.4", item.Name);
            Assert.AreEqual("Security", item.Category);
            Assert.AreEqual(LinuxDistro.Arch, item.LinuxDistro);
            Assert.IsFalse(item.IsWindows);

            // ISO + GNU-format sha256 sidecar, both derived from the release tag.
            Assert.AreEqual("https://iso.omarchy.org/omarchy-4.0.4.iso", item.DiskUri);
            Assert.AreEqual("https://iso.omarchy.org/omarchy-4.0.4.iso.sha256", item.ChecksumUri);
            Assert.AreEqual("sha256", item.ChecksumAlgorithm);
            Assert.AreEqual("ISO", item.FileType);
            Assert.IsFalse(item.NeedsExtraction);

            // Credentials consumed by the cidata seed builder at deploy time.
            Assert.AreEqual("omarchy", item.InitialUsername);
            Assert.IsFalse(string.IsNullOrEmpty(item.InitialPassword));
            Assert.AreEqual("false", item.SecureBoot);
            Assert.AreEqual("4.0.4", item.Version);

            // Unattended via the cidata seed ISO, so it stays visible without
            // the "Show ISO installers" toggle despite being ISO media.
            Assert.IsTrue(item.HasUnattendedInstallPath);
        }

        [TestMethod]
        public async Task LoadGalleryItems_StripsLeadingVFromTag()
        {
            var factory = FactoryFor(_ => Json(
                """{"tag_name":"v5.1.0","published_at":"2027-01-01T00:00:00Z","assets":[]}"""));

            var items = await new Omarchy(factory).LoadGalleryItems();

            Assert.AreEqual("https://iso.omarchy.org/omarchy-5.1.0.iso", items[0].DiskUri);
        }

        [TestMethod]
        public async Task LoadGalleryItems_MissingTagName_Throws()
        {
            var factory = FactoryFor(_ => Json("""{"message":"Not Found"}"""));

            await Assert.ThrowsAsync<Exception>(() => new Omarchy(factory).LoadGalleryItems());
        }

        [TestMethod]
        public async Task LoadGalleryItems_ServerError_Throws()
        {
            var factory = FactoryFor(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom")
            });

            await Assert.ThrowsAsync<HttpRequestException>(() => new Omarchy(factory).LoadGalleryItems());
        }
    }
}
