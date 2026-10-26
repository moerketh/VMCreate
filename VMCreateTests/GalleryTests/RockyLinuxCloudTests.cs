using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using VMCreate;
using VMCreate.Gallery;
using VMCreate.MediaHandlers;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Unit tests for the Rocky Linux cloud-image gallery loader (mocked HTTP):
    /// the card's item contract, the Azure-Base version scrape, and the
    /// two-major release-variant model (Rocky 9 default, Rocky 10).
    /// </summary>
    [TestClass]
    public sealed class RockyLinuxCloudTests
    {
        private static IHttpClientFactory FactoryReturning(string nineHtml, string tenHtml)
        {
            var mock = new Mock<IHttpClientFactory>();
            mock.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(new DirectoryHandler(nineHtml, tenHtml), disposeHandler: false));
            return mock.Object;
        }

        /// <summary>Serves each major's directory listing by URL path.</summary>
        private sealed class DirectoryHandler : HttpMessageHandler
        {
            private readonly string _nineHtml;
            private readonly string _tenHtml;

            public DirectoryHandler(string nineHtml, string tenHtml)
            {
                _nineHtml = nineHtml;
                _tenHtml = tenHtml;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                bool isTen = request.RequestUri?.AbsolutePath.Contains("/10/images/", StringComparison.Ordinal) == true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(isTen ? _tenHtml : _nineHtml)
                });
            }
        }

        private static string NineListing(params string[] files) =>
            string.Join("", files.Select(f => $"<a href=\"{f}\">{f}</a>"));

        // ── Item contract ─────────────────────────────────────────────────────

        [TestMethod]
        public async Task LoadGalleryItems_ReturnsOneCloudInitItemWithBothMajors()
        {
            var html = NineListing(
                "Rocky-9-Azure-Base-9.8-20260525.0.x86_64.vhdfixed.xz",
                "Rocky-10-Azure-Base-10.2-20260525.0.x86_64.vhdfixed.xz");

            var items = await new RockyLinuxCloud(FactoryReturning(html, html)).LoadGalleryItems();

            Assert.AreEqual(1, items.Count);
            var item = items[0];

            Assert.AreEqual("Rocky Linux (Cloud Image)", item.Name);
            Assert.IsTrue(item.HasTag("cloud-init"), "cloud-init tag drives the NoCloud seed path");
            Assert.IsTrue(item.HasTag("headless"), "headless tag gates RDP/SSH-removal steps");
            Assert.IsFalse(item.HasTag("elastic-siem"),
                "the vanilla card must not drag in the required Elastic Stack install step");
            Assert.AreEqual(LinuxDistro.Rocky, item.LinuxDistro);
            Assert.IsNull(item.Category, "Rocky is a general-purpose distro, not a Security card");
            Assert.AreEqual("VHD", item.FileTypeOverride);
            Assert.IsTrue(item.NeedsExtraction, "the .xz wrapper must trigger extraction");
            Assert.AreEqual(DiskImageFormat.Vhd, DiskFileDetector.ResolveDiskImageFormat(item, item.DiskUri),
                "the override must select the VHD media handler despite the .vhdfixed.xz name");

            var variants = item.ReleaseVariants!;
            Assert.AreEqual(2, variants.Count, "Rocky 9 + Rocky 10 variants");
            Assert.AreEqual("rocky-9", variants[0].Id);
            Assert.IsTrue(variants[0].IsDefault, "Rocky 9 is the card's own release (F2's target)");
            Assert.AreEqual("rocky-10", variants[1].Id);
            Assert.IsFalse(variants[1].IsDefault);
        }

        [TestMethod]
        public async Task LoadGalleryItems_DefaultVariantIsRocky9AzureBase()
        {
            var html = NineListing("Rocky-9-Azure-Base-9.8-20260525.0.x86_64.vhdfixed.xz");

            var items = await new RockyLinuxCloud(FactoryReturning(html, html)).LoadGalleryItems();
            var item = items[0];

            Assert.IsTrue(item.DiskUri.EndsWith("Rocky-9-Azure-Base.latest.x86_64.vhdfixed.xz", StringComparison.Ordinal),
                "the item describes the default (Rocky 9) release");
            Assert.IsTrue(item.ChecksumUri!.EndsWith("Rocky-9-Azure-Base.latest.x86_64.vhdfixed.xz.CHECKSUM", StringComparison.Ordinal));
            StringAssert.Contains(item.Version, "9.8-20260525.0");

            // Null id resolves to the item itself — the published fields are
            // authoritative for the default variant.
            Assert.AreSame(item, item.ResolveRelease(null));
            Assert.AreSame(item, item.ResolveRelease("rocky-9"));
        }

        [TestMethod]
        public async Task LoadGalleryItems_Rocky10Variant_ResolvesToConcreteAzureBaseClone()
        {
            var nine  = NineListing("Rocky-9-Azure-Base-9.8-20260525.0.x86_64.vhdfixed.xz");
            var ten   = NineListing("Rocky-10-Azure-Base-10.2-20260525.0.x86_64.vhdfixed.xz");

            var item = (await new RockyLinuxCloud(FactoryReturning(nine, ten)).LoadGalleryItems())[0];

            var resolved = item.ResolveRelease("rocky-10")!;
            Assert.IsNotNull(resolved);
            Assert.AreNotSame(item, resolved);
            Assert.IsTrue(resolved.DiskUri.EndsWith("Rocky-10-Azure-Base.latest.x86_64.vhdfixed.xz", StringComparison.Ordinal));
            Assert.IsTrue(resolved.ChecksumUri!.EndsWith("Rocky-10-Azure-Base.latest.x86_64.vhdfixed.xz.CHECKSUM", StringComparison.Ordinal));
            StringAssert.Contains(resolved.Version, "10.2-20260525.0");
            Assert.AreEqual(LinuxDistro.Rocky, resolved.LinuxDistro);
            Assert.IsNull(resolved.ReleaseVariants, "a resolved clone is a concrete image, not a picker");
            Assert.AreEqual("VHD", resolved.FileTypeOverride,
                "the override must survive variant resolution (vhdfixed.xz inference yields Other)");
            Assert.AreEqual(DiskImageFormat.Vhd, DiskFileDetector.ResolveDiskImageFormat(resolved, resolved.DiskUri));
            Assert.IsTrue(resolved.HasTag("cloud-init"), "the seed path applies to either major");
            Assert.IsTrue(resolved.HasTag("headless"));
        }

        // ── Version scrape ────────────────────────────────────────────────────

        [TestMethod]
        public async Task LoadGalleryItems_PicksNewestBuildPerMajor_ExcludesTwinFlavours()
        {
            var nine = NineListing(
                "Rocky-9-Azure-Base-9.7-20260101.0.x86_64.vhdfixed.xz",
                "Rocky-9-Azure-Base-9.8-20260525.0.x86_64.vhdfixed.xz",
                "Rocky-9-Azure-LVM-9.8-20260525.0.x86_64.vhdfixed.xz",
                "Rocky-9-GenericCloud-Base-9.8-20260525.0.x86_64.qcow2");
            var ten = NineListing(
                "Rocky-10-Azure-Base-10.2-20260401.0.x86_64.vhdfixed.xz",
                "Rocky-10-Azure-Base-10.2-20260525.0.x86_64.vhdfixed.xz",
                "Rocky-10-Azure-LVM-10.2-20260525.0.x86_64.vhdfixed.xz",
                "Rocky-10-GenericCloud-Base-10.2-20260525.0.x86_64.qcow2");

            var item = (await new RockyLinuxCloud(FactoryReturning(nine, ten)).LoadGalleryItems())[0];

            StringAssert.Contains(item.Version, "9.8-20260525.0",
                "the newest Rocky 9 Azure-Base build wins");
            var rocky10 = item.ReleaseVariants!.Single(v => v.Id == "rocky-10");
            StringAssert.Contains(rocky10.Version, "10.2-20260525.0",
                "the newest Rocky 10 Azure-Base build wins");
            // LVM (disk-layout) and GenericCloud (KVM-flavour) twins must not
            // leak into the Azure-Base scrape of either major.
            StringAssert.DoesNotMatch(item.Version, new System.Text.RegularExpressions.Regex(@"LVM|GenericCloud"));
        }

        [TestMethod]
        public async Task LoadGalleryItems_IndexFailure_StillReturnsItemWithBothVariants()
        {
            // Metadata scraping is best-effort; the download URLs are static.
            var items = await new RockyLinuxCloud(FactoryReturning("", "")).LoadGalleryItems();

            Assert.AreEqual(1, items.Count);
            var item = items[0];

            StringAssert.Contains(item.Version, "unknown");
            Assert.IsTrue(item.DiskUri.EndsWith("Rocky-9-Azure-Base.latest.x86_64.vhdfixed.xz", StringComparison.Ordinal));
            Assert.IsTrue(item.ChecksumUri!.EndsWith(".CHECKSUM", StringComparison.Ordinal));
            Assert.AreEqual(2, item.ReleaseVariants!.Count,
                "a metadata outage must not drop a variant — both latest-aliases stay deployable");
            var rocky10 = item.ReleaseVariants.Single(v => v.Id == "rocky-10");
            Assert.IsTrue(rocky10.DiskUri.EndsWith("Rocky-10-Azure-Base.latest.x86_64.vhdfixed.xz", StringComparison.Ordinal));
            Assert.AreEqual("unknown", rocky10.Version);
        }

        [TestMethod]
        public async Task LoadGalleryItems_DatePartOfBuildIdBecomesLastUpdated()
        {
            var nine = NineListing("Rocky-9-Azure-Base-9.8-20260525.0.x86_64.vhdfixed.xz");

            var item = (await new RockyLinuxCloud(FactoryReturning(nine, nine)).LoadGalleryItems())[0];

            StringAssert.Contains(item.LastUpdated, "2026-05-25",
                "the build id's yyyymmdd segment surfaces as the release date");
        }

        // ── Step gating ───────────────────────────────────────────────────────

        [TestMethod]
        public void HeadlessTag_GatesRdpAndCleanupSteps()
        {
            var item = new GalleryItem
            {
                Name = "Rocky Linux (Cloud Image)",
                DiskUri = "https://dl.rockylinux.org/pub/rocky/9/images/x86_64/Rocky-9-Azure-Base.latest.x86_64.vhdfixed.xz",
                Tags = new List<string> { "cloud-init", "headless" },
                LinuxDistro = LinuxDistro.Rocky,
            };

            Assert.IsFalse(new AutoRdpBackendResolveStep().IsApplicable(
                item, new VmCustomizations { RdpBackend = RdpBackend.Auto }));
            Assert.IsFalse(new InstallXrdpPostBootStep().IsApplicable(
                item, new VmCustomizations { RdpBackend = RdpBackend.Xrdp }));
            Assert.IsFalse(new InstallLamcoRdpStep().IsApplicable(
                item, new VmCustomizations { RdpBackend = RdpBackend.Lamco }));
            Assert.IsFalse(new RemoveAutomationUserStep().IsApplicable(
                item, new VmCustomizations()));
            Assert.IsFalse(new DisableSshStep().IsApplicable(
                item, new VmCustomizations()));
        }
    }
}