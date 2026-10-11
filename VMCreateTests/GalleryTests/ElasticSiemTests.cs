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
            var html = "<a href=\"Rocky-10-GenericCloud-Base-10.2-20260525.0.x86_64.qcow2\">x</a>";
            var items = await new ElasticSiem(FactoryReturning(html)).LoadGalleryItems();

            Assert.AreEqual(1, items.Count);
            var item = items[0];

            Assert.IsTrue(item.HasTag("cloud-init"), "cloud-init tag drives the NoCloud seed path");
            Assert.IsTrue(item.HasTag("headless"), "headless tag gates RDP/SSH-removal steps");
            Assert.IsTrue(item.HasTag("elastic-siem"));
            Assert.AreEqual(LinuxDistro.Rocky, item.LinuxDistro);
            Assert.AreEqual("Security", item.Category);
        }

        [TestMethod]
        public async Task LoadGalleryItems_PicksNewestVersionFromFileNames()
        {
            var html = "<a href=\"Rocky-10-GenericCloud-Base-10.2-20260401.0.x86_64.qcow2\">x</a>" +
                       "<a href=\"Rocky-10-GenericCloud-Base-10.2-20260525.0.x86_64.qcow2\">x</a>" +
                       "<a href=\"Rocky-10-GenericCloud-LVM-10.2-20260525.0.x86_64.qcow2\">x</a>";
            var item = (await new ElasticSiem(FactoryReturning(html)).LoadGalleryItems())[0];

            StringAssert.Contains(item.Version, "10.2-20260525.0");
            // The LVM variant must not leak into the Base version scrape.
        }

        [TestMethod]
        public async Task LoadGalleryItems_IndexFailure_StillReturnsItem()
        {
            // Metadata scraping is best-effort; the download URLs are static.
            var item = (await new ElasticSiem(FactoryReturning("")).LoadGalleryItems())[0];

            StringAssert.Contains(item.Version, "unknown");
            Assert.IsTrue(item.DiskUri.EndsWith("Rocky-10-GenericCloud-Base.latest.x86_64.qcow2", StringComparison.Ordinal));
            Assert.IsTrue(item.ChecksumUri.EndsWith(".CHECKSUM", StringComparison.Ordinal));
        }

        // ── Step gating ─────────────────────────────────────────────────────────

        private static GalleryItem ApplianceItem() => new()
        {
            Name = "Elastic SIEM",
            Publisher = "test",
            Description = "test",
            DiskUri = "https://x/Rocky-10-GenericCloud-Base.latest.x86_64.qcow2",
            SecureBoot = "false",
            EnhancedSessionTransportType = "HvSocket",
            Version = "Rocky 10",
            Tags = new List<string> { "cloud-init", "elastic-siem", "headless" },
            LinuxDistro = LinuxDistro.Rocky,
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

            Assert.IsFalse(step.IsVisibleFor(fedora), "rpm distros are not add-on targets until the dnf branch exists");
        }

        [TestMethod]
        public void DistroDetector_ClassifiesRockyFamily()
        {
            // Runtime re-verification path (guest os-release): exact IDs and
            // the RHEL family fallback.
            Assert.AreEqual(LinuxDistro.Rocky, DistroDetector.ClassifyForTest("rocky", "rhel centos fedora"));
            Assert.AreEqual(LinuxDistro.Rocky, DistroDetector.ClassifyForTest("almalinux", "rhel"));
            Assert.AreEqual(LinuxDistro.Rocky, DistroDetector.ClassifyForTest("some-rhel-derivative", "rhel like"));
            Assert.AreEqual(LinuxDistro.Unknown, DistroDetector.ClassifyForTest("gentoo", ""));
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

        // ── Guest setup script (content contract) ───────────────────────────────

        [TestMethod]
        public void BuildScript_ContainsBothPackageManagerBranches()
        {
            var script = ElasticStackInstaller.BuildScriptForTest(false, false, "", "", "");

            StringAssert.Contains(script, "PKGMGR=dnf", StringComparison.Ordinal);
            StringAssert.Contains(script, "PKGMGR=apt", StringComparison.Ordinal);
            StringAssert.Contains(script, "/etc/yum.repos.d/elastic-9.x.repo", StringComparison.Ordinal);
            StringAssert.Contains(script, "packages/9.x/yum", StringComparison.Ordinal);
            StringAssert.Contains(script, "packages/9.x/apt stable main", StringComparison.Ordinal);
            // The EL family (Rocky) is the appliance base — dnf must be accepted.
            StringAssert.Contains(script, "command -v dnf", StringComparison.Ordinal);
        }

        [TestMethod]
        public void BuildScript_NeverLeaksLlmKeyIntoPlainScriptSections()
        {
            // The AI key rides in the script (base64 transport), but must only
            // appear inside the inference.json heredoc — never in an echo/log
            // line that would land in stdout or the guest log.
            var script = ElasticStackInstaller.BuildScriptForTest(true, true, "https://llm/v1/chat/completions", "SUPER-SECRET-KEY", "test-model");

            var count = script.Split("SUPER-SECRET-KEY").Length - 1;
            Assert.AreEqual(1, count, "exactly one key occurrence expected (the inference payload)");
        }

        [TestMethod]
        public void BuildScript_AiDisabled_NoInferenceSection()
        {
            var script = ElasticStackInstaller.BuildScriptForTest(false, false, "", "", "");

            Assert.IsFalse(script.Contains("_inference/chat_completion", StringComparison.Ordinal));
        }

        [TestMethod]
        public void BuildScript_TrialOnlyWhenRequested()
        {
            var withTrial = ElasticStackInstaller.BuildScriptForTest(true, true, "u", "k", "m");
            var withoutTrial = ElasticStackInstaller.BuildScriptForTest(true, false, "u", "k", "m");

            StringAssert.Contains(withTrial, "start_trial", StringComparison.Ordinal);
            Assert.IsFalse(withoutTrial.Contains("start_trial?acknowledge", StringComparison.Ordinal));
        }

        [TestMethod]
        public void BuildScript_CredentialsFileAndIdempotencyMarkers()
        {
            var script = ElasticStackInstaller.BuildScriptForTest(false, false, "", "", "");

            StringAssert.Contains(script, "/opt/vmcreate-elastic/credentials.txt", StringComparison.Ordinal);
            StringAssert.Contains(script, "ELASTIC_SETUP_OK", StringComparison.Ordinal);
            StringAssert.Contains(script, ".installed", StringComparison.Ordinal);
            // Credentials must never be echoed: the file is written via redirection.
            Assert.IsFalse(script.Contains("echo \"${ES_PW}", StringComparison.Ordinal), "elastic password must not be echoed");
        }

        [TestMethod]
        public void BuildScript_BashSyntax_ValidWhenWslAvailable()
        {
            // WSL gives us a real bash for a syntax check (bash -n) on dev
            // machines; silently skipped where WSL is absent (CI containers).
            var wslPath = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "wsl.exe");
            if (!System.IO.File.Exists(wslPath))
            {
                Assert.Inconclusive("wsl.exe not present — bash syntax check skipped");
            }

            var script = ElasticStackInstaller.BuildScriptForTest(true, true, "https://llm/v1/chat/completions", "key", "model");
            var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"elastic-setup-{Guid.NewGuid():N}.sh");
            System.IO.File.WriteAllText(temp, script.Replace("\r\n", "\n"));

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = wslPath,
                    Arguments = $"-e bash -n \"{ToWslPath(temp)}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = System.Diagnostics.Process.Start(psi)!;
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(15000);

                Assert.AreEqual(0, process.ExitCode, $"bash -n rejected the generated script: {stderr}");
            }
            finally
            {
                try { System.IO.File.Delete(temp); } catch { }
            }
        }

        private static string ToWslPath(string windowsPath)
            => "/mnt/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');
    }
}
