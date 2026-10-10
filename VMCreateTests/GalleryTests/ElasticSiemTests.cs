using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using VMCreate.Gallery;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Unit tests for the Elastic SIEM gallery loader (mocked HTTP) and the
    /// two Elastic customization steps' gating logic (no guest required).
    /// </summary>
    [TestClass]
    public sealed class ElasticSiemTests
    {
        private static IHttpClientFactory FactoryReturning(string html)
        {
            var mock = new Mock<IHttpClientFactory>();
            mock.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(new FixedHandler(html), disposeHandler: false));
            return mock.Object;
        }

        private sealed class FixedHandler : HttpMessageHandler
        {
            private readonly string _html;
            public FixedHandler(string html) => _html = html;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_html)
                });
        }

        // ── ElasticSiem loader ──────────────────────────────────────────────────

        [TestMethod]
        public async Task LoadGalleryItems_ReturnsApplianceItemWithCloudInitTag()
        {
            var html = "<a href=\"20260801/\">x</a><a href=\"20260926/\">x</a><a href=\"current/\">x</a>";
            var items = await new ElasticSiem(FactoryReturning(html)).LoadGalleryItems();

            Assert.AreEqual(1, items.Count);
            var item = items[0];

            Assert.IsTrue(item.HasTag("cloud-init"), "cloud-init tag drives the NoCloud seed path");
            Assert.IsTrue(item.HasTag("headless"), "headless tag gates RDP/SSH-removal steps");
            Assert.IsTrue(item.HasTag("elastic-siem"));
            Assert.AreEqual(LinuxDistro.Ubuntu, item.LinuxDistro);
            Assert.AreEqual("Security", item.Category);
        }

        [TestMethod]
        public async Task LoadGalleryItems_PicksNewestBuildSerial()
        {
            var html = "<a href=\"20260101/\">x</a><a href=\"20261002/\">x</a><a href=\"20260405/\">x</a>";
            var item = (await new ElasticSiem(FactoryReturning(html)).LoadGalleryItems())[0];

            StringAssert.Contains(item.Version, "20261002");
        }

        [TestMethod]
        public async Task LoadGalleryItems_IndexFailure_StillReturnsItem()
        {
            // Metadata scraping is best-effort; the download URLs are static.
            var item = (await new ElasticSiem(FactoryReturning("")).LoadGalleryItems())[0];

            StringAssert.Contains(item.Version, "unknown");
            Assert.IsTrue(item.DiskUri.EndsWith("noble-server-cloudimg-amd64.img", StringComparison.Ordinal));
            Assert.IsNotNull(item.ChecksumUri);
        }

        // ── Step gating ─────────────────────────────────────────────────────────

        private static GalleryItem ApplianceItem() => new()
        {
            Name = "Elastic SIEM",
            Publisher = "test",
            Description = "test",
            DiskUri = "https://x/noble-server-cloudimg-amd64.img",
            SecureBoot = "false",
            EnhancedSessionTransportType = "HvSocket",
            Version = "24.04",
            Tags = new List<string> { "cloud-init", "elastic-siem", "headless" },
            LinuxDistro = LinuxDistro.Ubuntu,
        };

        private static GalleryItem UbuntuDesktopItem() => new()
        {
            Name = "Ubuntu 24.04 LTS",
            Publisher = "test",
            Description = "test",
            DiskUri = "https://x/ubuntu.vhdx.zip",
            SecureBoot = "false",
            EnhancedSessionTransportType = "HvSocket",
            Version = "24.04",
            LinuxDistro = LinuxDistro.Ubuntu,
        };

        [TestMethod]
        public void InstallStep_RequiredForAppliance_NoOptionSelectionNeeded()
        {
            var step = new ElasticStackInstallStep();
            var appliance = ApplianceItem();
            var noOptions = new VmCustomizations();

            Assert.IsTrue(step.IsVisibleFor(appliance));
            Assert.IsTrue(step.IsApplicable(appliance, noOptions), "appliance step must run without an option card");
            Assert.IsFalse(step.IsApplicable(UbuntuDesktopItem(), noOptions), "never runs on other distros");
        }

        [TestMethod]
        public void AddOnStep_VisibleOnAptFamilyDistros_DefaultOff()
        {
            var step = new ElasticStackAddOnStep();
            var noOptions = new VmCustomizations();

            Assert.IsTrue(step.IsVisibleFor(UbuntuDesktopItem()));
            Assert.IsFalse(step.DefaultEnabled, "installing a SIEM must be an explicit choice");
            Assert.IsFalse(step.IsApplicable(UbuntuDesktopItem(), noOptions));

            var enabled = new VmCustomizations();
            enabled.DistributionOptions.Add(new DistributionOptionSelection
            {
                Name = step.Name, IsEnabled = true, Order = 150,
            });
            Assert.IsTrue(step.IsApplicable(UbuntuDesktopItem(), enabled));
        }

        [TestMethod]
        public void AddOnStep_NotVisibleForAppliance_RequiredStepOwnsIt()
        {
            var step = new ElasticStackAddOnStep();

            Assert.IsFalse(step.IsVisibleFor(ApplianceItem()));
        }

        [TestMethod]
        public void AddOnStep_NotVisibleForNonAptDistros()
        {
            var step = new ElasticStackAddOnStep();
            var fedora = UbuntuDesktopItem();
            fedora.LinuxDistro = LinuxDistro.Fedora;

            Assert.IsFalse(step.IsVisibleFor(fedora));
        }

        [TestMethod]
        public void HeadlessTag_GatesRdpAndCleanupSteps()
        {
            var appliance = ApplianceItem();
            var autoBackend = new VmCustomizations { RdpBackend = RdpBackend.Auto };

            Assert.IsFalse(new AutoRdpBackendResolveStep().IsApplicable(appliance, autoBackend));
            Assert.IsFalse(new InstallXrdpPostBootStep().IsApplicable(
                appliance, new VmCustomizations { RdpBackend = RdpBackend.Xrdp }));
            Assert.IsFalse(new InstallLamcoRdpStep().IsApplicable(
                appliance, new VmCustomizations { RdpBackend = RdpBackend.Lamco }));
            Assert.IsFalse(new RemoveAutomationUserStep().IsApplicable(appliance, autoBackend));
            Assert.IsFalse(new DisableSshStep().IsApplicable(appliance, autoBackend));
        }

        // ── AI settings model ───────────────────────────────────────────────────

        [TestMethod]
        public void ElasticAiSettings_IsComplete_RequiresAllFields()
        {
            var ai = new ElasticAiSettings
            {
                Enabled = true,
                ChatCompletionsUrl = "https://llm/v1/chat/completions",
                ApiKey = "k",
                Model = "m",
            };
            Assert.IsTrue(ai.IsComplete);

            ai.ApiKey = null;
            Assert.IsFalse(ai.IsComplete);

            var disabled = new ElasticAiSettings { Enabled = false };
            Assert.IsFalse(disabled.IsComplete, "disabled AI is never complete regardless of fields");
        }
    }
}
