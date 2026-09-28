using System;
using System.Linq;
using System.Threading;
using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMCreate;
using VMCreate.Gallery;

namespace VMCreate.Tests.EndToEnd
{
    /// <summary>
    /// Deterministic headless end-to-end test against a REAL Hyper-V host:
    /// download → verify → extract → create VM → start → KVP heartbeat gate.
    /// The PowerShell-era equivalent of quickspawn's phase1_e2e.py — it
    /// exercises the full pipeline wiring that unit tests mock away, and
    /// would have caught bug classes like "image ships the agent unstarted".
    /// <para>
    /// Opt-in only — never runs in CI: set <c>VMCREATE_E2E=1</c> plus
    /// optionally <c>VMCREATE_E2E_LOADER</c> (loader type name substring,
    /// default <c>REMnux</c>) and <c>VMCREATE_E2E_KEEP_VM=1</c> to skip
    /// teardown for inspection.
    /// </para>
    /// Run: <c>dotnet test --filter TestCategory=E2E</c> as a Hyper-V
    /// Administrator on a host with internet access. Downloads are large
    /// (REMnux OVA ≈ 9 GB) — budget disk and time accordingly.
    /// </summary>
    [TestClass]
    [TestCategory("E2E")]
    public sealed class HyperVE2ETests
    {
        private ServiceProvider _services = null!;
        private string _vmBaseName = null!;

        [TestInitialize]
        public void RequireOptInAndBuildServices()
        {
            if (Environment.GetEnvironmentVariable("VMCREATE_E2E") != "1")
            {
                Assert.Inconclusive(
                    "E2E is opt-in: requires a real Hyper-V host. " +
                    "Set VMCREATE_E2E=1 (and optionally VMCREATE_E2E_LOADER / VMCREATE_E2E_KEEP_VM) " +
                    "and run as a Hyper-V Administrator.");
            }

            _services = new ServiceCollection()
                .AddLogging()
                .AddHttpClient()
                .AddCoreServices(typeof(SyncTimezoneStep).Assembly, typeof(BlackArch).Assembly)
                .BuildServiceProvider();
            _vmBaseName = $"vmcreate-e2e-{DateTime.Now:yyyyMMddHHmmss}";
        }

        [TestCleanup]
        public void Teardown()
        {
            if (Environment.GetEnvironmentVariable("VMCREATE_E2E") != "1" || _services == null)
                return;

            try
            {
                if (Environment.GetEnvironmentVariable("VMCREATE_E2E_KEEP_VM") != "1")
                {
                    var lifecycle = _services.GetRequiredService<IVmLifecycleManager>();
                    foreach (var vm in lifecycle
                                 .FindExistingVmsByBaseNameAsync(_vmBaseName, CancellationToken.None)
                                 .GetAwaiter().GetResult())
                    {
                        lifecycle.StopVMAsync(vm, CancellationToken.None).GetAwaiter().GetResult();
                        lifecycle.RemoveVMAsync(vm, CancellationToken.None).GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[e2e] teardown warning: {ex.Message} (VM base name: {_vmBaseName})");
            }
            finally
            {
                _services.Dispose();
            }
        }

        [TestMethod]
        [Timeout(int.MaxValue)]
        public async Task FullPipeline_DownloadToKvpHeartbeat_Succeeds()
        {
            string loaderHint = Environment.GetEnvironmentVariable("VMCREATE_E2E_LOADER") ?? "REMnux";

            // ── Arrange: pick the live gallery item from the chosen loader ──
            var aggregate = (AggregateGalleryLoader)_services.GetRequiredService<IGalleryLoader>();
            var allItems = await aggregate.LoadGalleryItems();
            var item = allItems.FirstOrDefault(i =>
                       i.Name?.IndexOf(loaderHint, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new AssertFailedException(
                    $"No gallery item matching '{loaderHint}' — loader may have failed. " +
                    $"Failed loaders: {string.Join(", ", aggregate.FailedLoaderNames)}");

            var vmSettings = new VmSettings
            {
                VMName = _vmBaseName,
                MemoryInMB = 4096,
                CPUCount = 2,
                ReplacePreviousVm = false
            };

            var progressLog = new ProgressCollector();
            var createVm = _services.GetRequiredService<CreateVM>();

            // ── Act: the entire pipeline, unmocked ───────────────────────────
            string createdVmName = await createVm.StartCreateVMAsync(
                vmSettings,
                new VmCustomizations(),
                item,
                CancellationToken.None,
                progressLog);

            // ── Assert: pipeline completed and the VM is really there ────────
            Assert.IsFalse(string.IsNullOrEmpty(createdVmName), "StartCreateVMAsync must return the VM name");
            StringAssert.StartsWith(createdVmName, _vmBaseName, StringComparison.Ordinal,
                "the timestamped VM name extends the requested base name");

            var lifecycle = _services.GetRequiredService<IVmLifecycleManager>();
            var existing = await lifecycle.FindExistingVmsByBaseNameAsync(_vmBaseName, CancellationToken.None);
            CollectionAssert.Contains(existing, createdVmName,
                $"created VM must be discoverable via the lifecycle manager (found: {string.Join(", ", existing)})");

            // The heartbeat gate already ran inside the pipeline when
            // integration services were enabled; re-confirm independently —
            // the point of the e2e is proving the guest agent answers.
            var heartbeat = _services.GetRequiredService<IKvpHeartbeatGate>();
            Assert.IsTrue(
                await heartbeat.WaitForGuestKvpAsync(createdVmName, CancellationToken.None, timeoutSeconds: 30),
                "guest KVP daemon must answer after deployment");

            Console.WriteLine($"[e2e] phases reached: {progressLog.PhasesReached}");
        }

        private sealed class ProgressCollector : IProgress<CreateVMProgressInfo>
        {
            public System.Collections.Generic.HashSet<VmDeploymentPhase> PhasesReached { get; } = new();

            public void Report(CreateVMProgressInfo value)
            {
                if (value.Phase != VmDeploymentPhase.None)
                    PhasesReached.Add(value.Phase);
                if (!string.IsNullOrEmpty(value.StepName))
                    Console.WriteLine($"[e2e] {value.Phase}: {value.StepName} ({value.ProgressPercentage}%)");
            }
        }
    }
}
