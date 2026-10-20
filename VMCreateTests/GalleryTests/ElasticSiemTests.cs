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
            var html = "<a href=\"Rocky-10-Azure-Base-10.2-20260525.0.x86_64.vhdfixed.xz\">x</a>";
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
            var html = "<a href=\"Rocky-10-Azure-Base-10.2-20260401.0.x86_64.vhdfixed.xz\">x</a>" +
                       "<a href=\"Rocky-10-Azure-Base-10.2-20260525.0.x86_64.vhdfixed.xz\">x</a>" +
                       "<a href=\"Rocky-10-Azure-LVM-10.2-20260525.0.x86_64.vhdfixed.xz\">x</a>" +
                       "<a href=\"Rocky-10-GenericCloud-Base-10.2-20260525.0.x86_64.qcow2\">x</a>";
            var item = (await new ElasticSiem(FactoryReturning(html)).LoadGalleryItems())[0];

            StringAssert.Contains(item.Version, "10.2-20260525.0");
            // The LVM variant and the KVM-flavour GenericCloud must not leak
            // into the Azure-Base version scrape.
        }

        [TestMethod]
        public async Task LoadGalleryItems_IndexFailure_StillReturnsItem()
        {
            // Metadata scraping is best-effort; the download URLs are static.
            var item = (await new ElasticSiem(FactoryReturning("")).LoadGalleryItems())[0];

            StringAssert.Contains(item.Version, "unknown");
            Assert.IsTrue(item.DiskUri.EndsWith("Rocky-10-Azure-Base.latest.x86_64.vhdfixed.xz", StringComparison.Ordinal));
            Assert.IsTrue(item.ChecksumUri.EndsWith(".CHECKSUM", StringComparison.Ordinal));
            Assert.IsTrue(item.NeedsExtraction, "the .xz wrapper must trigger extraction");
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
                Name = step.Name, IsEnabled = true,
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
        public void BuildScript_KibanaConfigReadableByKibanaUserAndPointsAtEsTls()
        {
            // Live E2E #2 on the Rocky appliance: `chmod 600` on a fresh
            // root:root kibana.yml made the kibana service user unable to
            // read its own config (FATAL EACCES). Vendor default is
            // root:kibana 0660. ES 9.x ships http TLS on, so kibana.yml
            // must also specify https hosts + the CA. The CA lives in
            // /etc/elasticsearch/certs (root:elasticsearch 0750) which the
            // kibana user cannot traverse — hence the copy into /etc/kibana.
            var script = ElasticStackInstaller.BuildScriptForTest(false, false, "", "", "");

            StringAssert.Contains(script, "chown root:kibana /etc/kibana/kibana.yml", StringComparison.Ordinal);
            StringAssert.Contains(script, "chmod 0660 /etc/kibana/kibana.yml", StringComparison.Ordinal);
            Assert.IsFalse(script.Contains("chmod 600 /etc/kibana", StringComparison.Ordinal), "600 on kibana config is vendor-divergent and breaks the kibana user");
            StringAssert.Contains(script, "elasticsearch.hosts: [\"https://localhost:9200\"]", StringComparison.Ordinal);
            StringAssert.Contains(script, "elasticsearch.ssl.certificateAuthorities: [\"/etc/kibana/http_ca.crt\"]", StringComparison.Ordinal);
            StringAssert.Contains(script, "install -o root -g kibana -m 0640 /etc/elasticsearch/certs/http_ca.crt /etc/kibana/http_ca.crt", StringComparison.Ordinal);
        }

        [TestMethod]
        public void BuildScript_HeapOptionsUseSafePrintfForm()
        {
            // `printf "-Xms${X}m\n…"` treats the leading-dash format string
            // as an option ('printf: -X: invalid option', observed live) and
            // silently writes an empty jvm options file. The '%s\n' form
            // cannot be misread.
            var script = ElasticStackInstaller.BuildScriptForTest(false, false, "", "", "");

            StringAssert.Contains(script, "printf '%s\\n' \"-Xms${HEAP_MB}m\" \"-Xmx${HEAP_MB}m\"", StringComparison.Ordinal);
            Assert.IsFalse(script.Contains("printf \"-Xms", StringComparison.Ordinal), "leading-dash printf format is parsed as an option by bash");
        }

        [TestMethod]
        public void BuildScript_IsCrLfFree_GuestBashRequiresLf()
        {
            // The script is composed with AppendLine (CRLF on Windows) and
            // runs under guest bash, which treats '\r' as part of the command
            // ('set: pipefail\r: invalid option name' — observed live on the
            // Rocky appliance E2E). Both branches (AI on/off) must be LF-only.
            foreach (var script in new[]
            {
                ElasticStackInstaller.BuildScriptForTest(false, false, "", "", ""),
                ElasticStackInstaller.BuildScriptForTest(true, true, "https://llm/v1/chat/completions", "key", "model"),
            })
            {
                Assert.IsFalse(script.Contains('\r'),
                    "guest script must not contain CR bytes — bash renders them as part of the command");
            }
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

            // wsl.exe SHIPS with Windows, so existence proves nothing about a
            // usable WSL: GitHub's windows runners have the binary but no
            // distro, and `wsl -e …` exits non-zero with empty command
            // stderr — indistinguishable from a bash -n rejection without a
            // preflight. Probe WSL itself; unusable means skip, not fail.
            var probe = new System.Diagnostics.ProcessStartInfo
            {
                FileName = wslPath,
                Arguments = "-e echo ok",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var probeProcess = System.Diagnostics.Process.Start(probe)!)
            {
                probeProcess.StandardOutput.ReadToEnd();
                probeProcess.WaitForExit(15000);
                if (probeProcess.ExitCode != 0)
                {
                    Assert.Inconclusive($"WSL present but unusable (probe exit {probeProcess.ExitCode}) — bash syntax check skipped");
                }
            }

            var script = ElasticStackInstaller.BuildScriptForTest(true, true, "https://llm/v1/chat/completions", "key", "model");
            var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"elastic-setup-{Guid.NewGuid():N}.sh");
            // No CRLF normalization here — the temp file must be byte-identical
            // to what the guest receives. bash -n on CRLF text fails exactly
            // like the guest did ("set: pipefail\r: invalid option name").
            System.IO.File.WriteAllText(temp, script);

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

        [TestMethod]
        public void BuildScript_FleetChain_MatchesLiveProvenSequence()
        {
            // The fleet section encodes five live-validated fixes from the
            // E2E #3 rig (Rocky 10 / Elastic 9.x):
            //   1. rpm flavor "servers" — the default flavor drops the
            //      fleet-server binary (elastic/elastic-agent#6542).
            //   2. service_tokens perms — ES 9.x ships root:root 0600, which
            //      401s every Fleet API call until chown/chmod.
            //   3. kibana encryption keys — without them Fleet APIs 503
            //      until three keys + agents.enabled + restart.
            //   4. has_fleet_server:true on the fleet-server policy — the
            //      agent stalls "Waiting on default policy" otherwise.
            //   5. enrollment token via the Kibana enrollment_api_keys API —
            //      `elasticsearch-create-enrollment-token` is invalid 9.x
            //      usage in this flow.
            var script = ElasticStackInstaller.BuildScriptForTest(false, false, "", "", "");

            // Defect #1 — flavor must be set before the install line runs.
            var flavorPos = script.IndexOf("export ELASTIC_AGENT_FLAVOR=servers", StringComparison.Ordinal);
            var installPos = script.IndexOf("elastic-agent\"", StringComparison.Ordinal);
            Assert.IsTrue(flavorPos >= 0, "flavor export missing");
            Assert.IsTrue(installPos >= 0, "package install line missing");
            Assert.IsTrue(flavorPos < installPos, "flavor must be exported before the agent package installs");

            // Defect #2 — perms fix before token creation.
            var permsPos = script.IndexOf("chmod 0660 /etc/elasticsearch/service_tokens", StringComparison.Ordinal);
            var tokenPos = script.IndexOf("elasticsearch-service-tokens create", StringComparison.Ordinal);
            Assert.IsTrue(permsPos >= 0, "service_tokens perms fix missing");
            Assert.IsTrue(tokenPos > permsPos, "token create must run after the perms fix");

            // Defect #3 — kibana encryption keys + fleet agents toggled on.
            StringAssert.Contains(script, "xpack.encryptedSavedObjects.encryptionKey:", StringComparison.Ordinal);
            StringAssert.Contains(script, "xpack.security.encryptionKey:", StringComparison.Ordinal);
            StringAssert.Contains(script, "xpack.reporting.encryptionKey:", StringComparison.Ordinal);
            StringAssert.Contains(script, "xpack.fleet.agents.enabled: true", StringComparison.Ordinal);

            // Fleet setup is retried via HTTP status code, not HTTPie output.
            StringAssert.Contains(script, "curl -s --max-time 30 -o /opt/vmcreate-elastic/fleet-setup.json -w '%{http_code}'", StringComparison.Ordinal);
            StringAssert.Contains(script, "/api/fleet/setup", StringComparison.Ordinal);

            // Defect #4 — the default fleet-server policy carries
            // has_fleet_server, and enroll consumes it (no policy flag).
            StringAssert.Contains(script, "\"has_fleet_server\":true", StringComparison.Ordinal);
            StringAssert.Contains(script, "timeout 180 elastic-agent enroll --force", StringComparison.Ordinal);
            StringAssert.Contains(script, "--fleet-server-service-token", StringComparison.Ordinal);

            // Health gate: the API that actually exists. The obsolete probe
            // URL may only ever appear inside a comment, never a command.
            StringAssert.Contains(script, "https://localhost:8220/api/status", StringComparison.Ordinal);
            foreach (var rawLine in script.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Contains("server-info", StringComparison.Ordinal))
                {
                    StringAssert.StartsWith(line, "#", "server-info is a 404 even when healthy — it must not be probed");
                }
            }

            // Defect #5 — enrollment key comes from Kibana, not the ES CLI.
            // This post embeds ${AGENT_POLICY_ID}, so unlike the single-quoted
            // static bodies it uses double-quoted bash with escaped JSON.
            StringAssert.Contains(script, "api/fleet/enrollment_api_keys", StringComparison.Ordinal);
            StringAssert.Contains(script, "--data \"{\\\"policy_id\\\":\\\"${AGENT_POLICY_ID}\\\",\\\"name\\\":\\\"VMCreate Agent Enrollment\\\"}\"", StringComparison.Ordinal);
            Assert.IsFalse(script.Contains("elasticsearch-create-enrollment-token", StringComparison.Ordinal), "9.x uses the Kibana enrollment_api_keys API");
            Assert.IsFalse(script.Contains("--auto-register", StringComparison.Ordinal));

            // The policy names pin the exact POST bodies.
            StringAssert.Contains(script, "\"name\":\"VMCreate Fleet Server Policy\"", StringComparison.Ordinal);
            StringAssert.Contains(script, "\"name\":\"VMCreate Agent Policy\"", StringComparison.Ordinal);
        }

        [TestMethod]
        public void BuildScript_FleetChain_BestEffort_NotFatal()
        {
            // Fleet availability is a soft requirement: any failure must
            // warn and continue to the AI block, never fail the VM. The
            // prompt-free enrollment (no --non-interactive, --force) must
            // still be protected against hanging the RunCommandAsync budget.
            var script = ElasticStackInstaller.BuildScriptForTest(false, false, "", "", "");

            Assert.IsFalse(script.Contains("--non-interactive", StringComparison.Ordinal), "enroll --force replaces the install prompt chain");
            // `elastic-agent install` legitimately appears in the credentials
            // line (client VMs enroll that way) — only the package-CLI-TUI
            // `elastic-agent install --non-interactive` chain must be gone.
            Assert.IsFalse(script.Contains("elastic-agent install --non-interactive", StringComparison.Ordinal));

            // --force is valid only on enroll. The service-tokens create must
            // be a plain create (live-proven: "--force is not a recognized
            // option" on the service-tokens CLI in 9.x).
            var createPos = script.IndexOf("elasticsearch-service-tokens create", StringComparison.Ordinal);
            Assert.IsTrue(createPos > 0);
            var createLineEnd = script.IndexOf('\n', createPos);
            var createLine = script[createPos..createLineEnd];
            Assert.IsFalse(createLine.Contains("--force", StringComparison.Ordinal), "service-tokens create --force is invalid in 9.x");

            // Warnings only — fleet failures cascade into WARN lines, not fail().
            var fleetStart = script.IndexOf("/api/fleet/setup", StringComparison.Ordinal);
            Assert.IsTrue(fleetStart > 0, "fleet section not found");
            var tail = script[fleetStart..];
            var failCalls = System.Text.RegularExpressions.Regex.Matches(tail, @"\bfail\(\s*\)");
            Assert.AreEqual(0, failCalls.Count, "fleet chain failures must warn, not fail()");
        }

        [TestMethod]
        public void BuildScript_FleetChain_ConditionalCredentials()
        {
            // credentials.txt must only claim what was verified. Both lines
            // live under the FLEET_HEALTHY gate; inside that block the fleet
            // server address is always written, while the enrollment command
            // is gated on AGENT_TOKEN having been successfully minted.
            var script = ElasticStackInstaller.BuildScriptForTest(false, false, "", "", "");

            var credPos = script.IndexOf("Fleet server:", StringComparison.Ordinal);
            Assert.IsTrue(credPos > 0, "fleet server credentials line missing");
            var credLineEnd = script.IndexOf('\n', credPos);
            StringAssert.Contains(script[credPos..credLineEnd], "https://${GUEST_IP:-<vm-ip>}:8220", StringComparison.Ordinal);

            var enrollPos = script.IndexOf("Agent enrollment:", StringComparison.Ordinal);
            Assert.IsTrue(enrollPos > 0, "agent enrollment credentials line missing");
            // The gate is a one-line guard on the SAME line, before the echo.
            var lineStart = script.LastIndexOf('\n', enrollPos) + 1;
            var enrollLineEnd = script.IndexOf('\n', enrollPos);
            var enrollLine = script[lineStart..enrollLineEnd].Trim();
            StringAssert.Contains(enrollLine, "elastic-agent install --url=https://${GUEST_IP:-<vm-ip>}:8220", StringComparison.Ordinal);
            StringAssert.Contains(enrollLine, "--enrollment-token=${AGENT_TOKEN}", StringComparison.Ordinal);
            StringAssert.StartsWith(enrollLine, "[ -n \"${AGENT_TOKEN:-}\" ]", StringComparison.Ordinal, "enrollment credentials line must be gated on the minted token");
        }

        private static string ToWslPath(string windowsPath)
            => "/mnt/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');
    }
}
