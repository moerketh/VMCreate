using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;
using VMCreate.Gallery;
using VMCreate.Gallery.distributions;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Tests for the <see cref="Ubuntu"/> discovery loader. All HTTP is
    /// mocked with canned distro-info CSV, mirror index and directory
    /// listings shaped like the live sources; no network is needed.
    /// </summary>
    [TestClass]
    public sealed class UbuntuLoaderTests
    {
        // distro-info-data ubuntu.csv subset: an EOL series, the two
        // supported LTS series on the mirror, a supported interim series
        // with no Hyper-V image, and an unreleased future series.
        private const string Csv =
            """
            version,codename,series,created,release,eol,eol-server,eol-esm,eol-legacy
            20.04 LTS,Focal Fossa,focal,2019-10-10,2020-04-23,2025-04-23,2025-04-23,2030-04-23,2035-04-25
            22.04 LTS,Jammy Jellyfish,jammy,2021-10-14,2022-04-21,2027-06-01,2027-06-01,2032-04-21,2037-04-30
            24.04 LTS,Noble Numbat,noble,2023-10-12,2024-04-25,2029-05-31,2029-05-31,2034-04-25,2039-04-28
            26.04 LTS,Resolute Raccoon,resolute,2025-10-09,2026-04-23,2031-05-29,2031-05-29,2036-04-23,2041-04-30
            26.10,Stonking Stingray,stonking,2026-04-24,2026-10-15,2027-07-15
            27.04,Tenacious T-Rex,tenacious,2026-10-16,2027-04-22,2028-01-20
            """;

        // Mirror index: only jammy, noble and resolute carry Hyper-V images
        // ("daily" and "bionic" exercise that non-series dirs are ignored;
        // focal proves EOL series are never requested).
        private const string MirrorIndex =
            """
            <tr><td><a href="bionic/">bionic/</a></td></tr>
            <tr><td><a href="daily/">daily/</a></td></tr>
            <tr><td><a href="focal/">focal/</a></td></tr>
            <tr><td><a href="jammy/">jammy/</a></td></tr>
            <tr><td><a href="noble/">noble/</a></td></tr>
            <tr><td><a href="resolute/">resolute/</a></td></tr>
            <tr><td><a href="/hyper-v/desktop/">Parent Directory</a></td></tr>
            <tr><td><a href="?C=N;O=D">Name</a></td></tr>
            """;

        private static string ChannelListing(string series, string arch) =>
            $"""
            <tr><td><a href="SHA256SUMS">SHA256SUMS</a></td></tr>
            <tr><td><a href="ubuntu-{series}-hyperv-{arch}-ubuntu-desktop-hyperv.vhdx.zip">ubuntu-{series}-hyperv-{arch}-ubuntu-desktop-hyperv.vhdx.zip</a></td></tr>
            <tr><td><a href="ubuntu-{series}-hyperv-{arch}-ubuntu-desktop-hyperv.vhdx.zip.manifest">manifest</a></td></tr>
            <tr><td><a href="unpacked/">unpacked/</a></td></tr>
            """;

        private const string ReleasesIsoPage =
            """
            <a href="ubuntu-26.10-desktop-amd64.iso">64-bit PC (AMD64) desktop image</a>
            <tr><td><a href="ubuntu-26.10-desktop-amd64.iso">ubuntu-26.10-desktop-amd64.iso</a></td><td>5.2G</td></tr>
            <tr><td><a href="ubuntu-26.10-beta-desktop-amd64.iso">ubuntu-26.10-beta-desktop-amd64.iso</a></td><td>5.9G</td></tr>
            <tr><td><a href="SHA256SUMS">SHA256SUMS</a></td></tr>
            """;

        /// <summary>
        /// Routes each request by URI: the CSV, the mirror index, per-series
        /// GA/daily channel listings (amd64 vs the resolute amd64v3 rename),
        /// build-info serials, and the releases.ubuntu.com ISO page.
        /// </summary>
        private static IHttpClientFactory RoutingFactory() => FactoryFor(RouteRequest);

        private static HttpResponseMessage RouteRequest(HttpRequestMessage req)
        {
            var url = req.RequestUri!.ToString();
            var ok = new Func<string, HttpResponseMessage>(body => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
                RequestMessage = req
            });
            var notFound = new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req };

            if (url.Contains("ubuntu.csv")) return ok(Csv);
            if (url.EndsWith("hyper-v/desktop/")) return ok(MirrorIndex);

            if (url.EndsWith("jammy/release/current/")) return ok(ChannelListing("jammy", "amd64"));
            if (url.EndsWith("jammy/current/")) return ok(ChannelListing("jammy", "amd64"));
            if (url.EndsWith("jammy/release/current/unpacked/build-info.txt")) return ok("SERIAL=20220505\n");
            if (url.EndsWith("jammy/current/unpacked/build-info.txt")) return ok("SERIAL=20261021\n");

            if (url.EndsWith("noble/release/current/")) return ok(ChannelListing("noble", "amd64"));
            if (url.EndsWith("noble/current/")) return ok(ChannelListing("noble", "amd64"));
            if (url.EndsWith("noble/release/current/unpacked/build-info.txt")) return ok("SERIAL=20240706\n");
            if (url.EndsWith("noble/current/unpacked/build-info.txt")) return ok("SERIAL=20261022\n");

            // Resolute: daily channel only (no GA yet) with the amd64v3 rename.
            if (url.EndsWith("resolute/release/current/")) return notFound;
            if (url.EndsWith("resolute/current/")) return ok(ChannelListing("resolute", "amd64v3"));
            if (url.EndsWith("resolute/release/current/unpacked/build-info.txt")) return notFound;
            if (url.EndsWith("resolute/current/unpacked/build-info.txt")) return ok("SERIAL=20261022\n");

            // Stonking: not on the mirror; desktop ISO on releases.ubuntu.com.
            if (url.EndsWith("releases.ubuntu.com/26.10/")) return ok(ReleasesIsoPage);

            return notFound;
        }

        [TestMethod]
        public async Task Discovery_ReturnsOneCardPerSupportedSeries_NewestFirst()
        {
            var items = await new Ubuntu(new Mock<ILogger<Ubuntu>>().Object, RoutingFactory()).LoadGalleryItems();

            CollectionAssert.AreEqual(
                new[] { "Ubuntu 26.10", "Ubuntu 26.04 LTS", "Ubuntu 24.04 LTS", "Ubuntu 22.04 LTS" },
                items.Select(i => i.Name).ToList());

            foreach (var item in items)
            {
                Assert.AreEqual("Canonical Group Ltd", item.Publisher);
                Assert.AreEqual(LinuxDistro.Ubuntu, item.LinuxDistro);
                // SHA256SUMS rides the same mirror origin as the image:
                // transport-grade verification only, so the card badges as
                // unverified under the pinned-digest badge contract.
                Assert.IsFalse(item.HasChecksum, $"{item.Name} must not badge as verified for a mirror-served digest");
                Assert.IsFalse(string.IsNullOrEmpty(item.ChecksumUri), $"{item.Name} must wire SHA256SUMS so its download is consistency-checked");
                Assert.AreEqual("sha256", item.ChecksumAlgorithm, ignoreCase: true, message: $"{item.Name} must verify with SHA-256");
            }
        }

        [TestMethod]
        public async Task Discovery_GaSeries_CarriesDailyVariantWithOwnChecksum()
        {
            var items = await new Ubuntu(new Mock<ILogger<Ubuntu>>().Object, RoutingFactory()).LoadGalleryItems();
            var noble = items.Single(i => i.Version == "24.04 LTS");

            // The card itself describes the GA build…
            StringAssert.Contains(noble.DiskUri, "/noble/release/current/");
            StringAssert.EndsWith(noble.ChecksumUri, "noble/release/current/SHA256SUMS");

            // …and offers the refreshed daily build as a non-default variant.
            var daily = noble.ReleaseVariants!.Single();
            Assert.AreEqual("daily", daily.Id);
            Assert.IsFalse(daily.IsDefault);
            StringAssert.Contains(daily.DiskUri, "/noble/current/");
            StringAssert.EndsWith(daily.ChecksumUri, "noble/current/SHA256SUMS");

            var resolved = noble.ResolveRelease("daily")!;
            StringAssert.Contains(resolved.DiskUri, "/noble/current/");
            StringAssert.EndsWith(resolved.ChecksumUri, "noble/current/SHA256SUMS");
        }

        [TestMethod]
        public async Task Discovery_DailyOnlySeries_ScrapesRenamedFileAndSkipsGaVariant()
        {
            var items = await new Ubuntu(new Mock<ILogger<Ubuntu>>().Object, RoutingFactory()).LoadGalleryItems();
            var resolute = items.Single(i => i.Version == "26.04 LTS");

            // The amd64v3 filename is scraped from the listing, not constructed.
            StringAssert.Contains(resolute.DiskUri, "amd64v3");
            StringAssert.Contains(resolute.DiskUri, "/resolute/current/");

            // With no GA build there is nothing to choose between — no variants.
            Assert.IsNull(resolute.ReleaseVariants);
            Assert.AreEqual(new DateTime(2026, 10, 22).ToLongDateString(), resolute.LastUpdated);
        }

        [TestMethod]
        public async Task Discovery_SeriesWithoutMirrorImage_FallsBackToDesktopIso()
        {
            var items = await new Ubuntu(new Mock<ILogger<Ubuntu>>().Object, RoutingFactory()).LoadGalleryItems();
            var interim = items.Single(i => i.Version == "26.10");

            StringAssert.EndsWith(interim.DiskUri, "releases.ubuntu.com/26.10/ubuntu-26.10-desktop-amd64.iso");
            StringAssert.EndsWith(interim.ChecksumUri, "releases.ubuntu.com/26.10/SHA256SUMS");
            Assert.AreEqual("ISO", interim.FileType);

            // The beta ISO on the same page must not be picked.
            Assert.IsFalse(interim.DiskUri.Contains("beta", StringComparison.Ordinal), $"picked beta ISO: {interim.DiskUri}");
        }

        [TestMethod]
        public async Task Discovery_Filtering_EolUnreleasedAndNonSeriesDirsIgnored()
        {
            var items = await new Ubuntu(new Mock<ILogger<Ubuntu>>().Object, RoutingFactory()).LoadGalleryItems();

            Assert.IsFalse(items.Any(i => i.DiskUri.Contains("focal")), "EOL series must not appear");
            Assert.IsFalse(items.Any(i => i.DiskUri.Contains("tenacious")), "unreleased series must not appear");
            Assert.IsFalse(items.Any(i => i.DiskUri.Contains("daily/")), "the mirror's non-series 'daily' dir must not appear");
        }

        [TestMethod]
        public async Task Discovery_FailureOnAnySource_FallsBackToPinnedGaPair()
        {
            var factory = FactoryFor(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

            var items = await new Ubuntu(new Mock<ILogger<Ubuntu>>().Object, factory).LoadGalleryItems();

            CollectionAssert.AreEqual(new[] { "Ubuntu 22.04 LTS", "Ubuntu 24.04 LTS" }, items.Select(i => i.Name).ToList());
            foreach (var item in items)
            {
                StringAssert.Contains(item.DiskUri, "/release/current/");
                // Mirror-served SHA256SUMS: consistency check, not a pinned
                // publisher digest, so the card badges as unverified.
                Assert.IsFalse(item.HasChecksum);
                Assert.IsFalse(string.IsNullOrEmpty(item.ChecksumUri), "fallback items must still wire SHA256SUMS");
            }
        }

        [TestMethod]
        public async Task Discovery_EmptyGallery_FallsBackToPinnedGaPair()
        {
            // CSV parses but yields no supported series (header only).
            var factory = FactoryFor(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("version,codename,series,created,release,eol\n")
            });

            var items = await new Ubuntu(new Mock<ILogger<Ubuntu>>().Object, factory).LoadGalleryItems();

            Assert.AreEqual(2, items.Count, "an empty discovery must degrade to the pinned pair, not an empty gallery");
        }

        [TestMethod]
        public async Task Discovery_ChannelBuildSerial_BecomesLastUpdated()
        {
            var items = await new Ubuntu(new Mock<ILogger<Ubuntu>>().Object, RoutingFactory()).LoadGalleryItems();
            var jammy = items.Single(i => i.Version == "22.04 LTS");

            // The GA channel's serial (20220505), formatted — and the daily
            // variant's own serial (20261021), not the GA's.
            Assert.AreEqual(new DateTime(2022, 5, 5).ToLongDateString(), jammy.LastUpdated);
            Assert.AreEqual(new DateTime(2026, 10, 21).ToLongDateString(), jammy.ReleaseVariants!.Single().LastUpdated);
        }

        [TestMethod]
        public async Task Discovery_PerRequestTimeoutOnOneChannel_DegradesThatChannelOnly()
        {
            // A per-request HttpClient timeout surfaces as TaskCanceledException
            // while the loader's own token stays live. It must drop only the
            // timed-out channel — noble's GA fetch here — not the whole
            // gallery: noble still appears from its daily channel and every
            // other series is untouched, instead of the whole discovery
            // collapsing to the pinned fallback pair.
            var factory = FactoryFor(req =>
            {
                if (req.RequestUri!.ToString().EndsWith("noble/release/current/"))
                    throw new TaskCanceledException("simulated per-request timeout");
                return RouteRequest(req);
            });

            var items = await new Ubuntu(new Mock<ILogger<Ubuntu>>().Object, factory).LoadGalleryItems();

            CollectionAssert.AreEqual(
                new[] { "Ubuntu 26.10", "Ubuntu 26.04 LTS", "Ubuntu 24.04 LTS", "Ubuntu 22.04 LTS" },
                items.Select(i => i.Name).ToList(),
                "one timed-out channel must not collapse discovery to the pinned fallback");

            var noble = items.Single(i => i.Version == "24.04 LTS");
            StringAssert.Contains(noble.DiskUri, "/noble/current/",
                "the GA channel timed out, so the card falls back to the daily build");
        }

        #region Mock HTTP factory

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

        #endregion
    }
}
