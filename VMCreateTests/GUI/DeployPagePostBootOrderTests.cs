using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using VMCreate;
using VMCreate.Gallery;
using VMCreate.HyperV.VmCreation;

namespace VMCreate.Tests.GUI
{
    /// <summary>
    /// Pins the deploy page's post-boot card list to the backend's execution
    /// order. The view model builds its cards from
    /// <see cref="IPostBootStepPlanner"/> — the same ordering
    /// <see cref="PostBootCustomizationService"/> executes with — so this suite
    /// fails the moment either side drifts: a new step without a card, a
    /// re-ordered list, a card whose step never runs, or a step whose card is
    /// missing from the list entirely.
    /// </summary>
    [TestClass]
    public class DeployPagePostBootOrderTests
    {
        private static readonly IReadOnlyList<ICustomizationStep> Steps = DiscoverSteps();
        private static readonly IPostBootStepPlanner Planner = new PostBootStepPlanner(Steps);

        private static GalleryItem KaliDisk() => new()
        {
            Name = "Kali Linux",
            DiskUri = "http://example.com/kali.vhdx",
            LinuxDistro = LinuxDistro.Kali,
        };

        [TestMethod]
        public void CardOrder_MatchesPlannerOrder_XrdpWithTimezoneVpn()
        {
            var wizard = NewWizard(KaliDisk(), new VmCustomizations
            {
                RdpBackend = RdpBackend.Xrdp,
                SyncTimezone = true,
                InstallOpenVpn = true,
                ConfigureHtbVpn = true,
                EnableIntegrationServices = true,
            });

            var vm = new DeployPageViewModel(wizard, NullLogger.Instance, configurableSteps: null, Planner);

            AssertCardOrderMatchesPlanner(vm, wizard, StepPlatform.Linux, expectTempNic: true, expectKvp: true);
        }

        [TestMethod]
        public void CardOrder_MatchesPlannerOrder_Lamco()
        {
            var wizard = NewWizard(KaliDisk(), new VmCustomizations
            {
                RdpBackend = RdpBackend.Lamco,
                EnableIntegrationServices = false,
            });

            var vm = new DeployPageViewModel(wizard, NullLogger.Instance, configurableSteps: null, Planner);

            AssertCardOrderMatchesPlanner(vm, wizard, StepPlatform.Linux, expectTempNic: true, expectKvp: false);
        }

        [TestMethod]
        public void CardOrder_MatchesPlannerOrder_Auto()
        {
            // Auto: the resolver card appears; branch cards appear at runtime
            // via ReconcilePostBootSteps (covered separately).
            var wizard = NewWizard(KaliDisk(), new VmCustomizations { RdpBackend = RdpBackend.Auto });

            var vm = new DeployPageViewModel(wizard, NullLogger.Instance, configurableSteps: null, Planner);

            AssertCardOrderMatchesPlanner(vm, wizard, StepPlatform.Linux, expectTempNic: true, expectKvp: true);
        }

        [TestMethod]
        public void Reconcile_AfterResolverFlipsToLamco_InsertsBranchCards_RemovesXrdpBlock()
        {
            // The Auto re-plan: the resolver (order 232) mutates the shared
            // customizations. On the NEXT step report the presenter calls
            // ReconcilePostBootSteps — the Lamco branch cards must appear
            // right after the resolver card, and the pending xrdp-block cards
            // (240-270, now inapplicable) must be removed.
            var wizard = NewWizard(KaliDisk(), new VmCustomizations { RdpBackend = RdpBackend.Auto });
            var vm = new DeployPageViewModel(wizard, NullLogger.Instance, configurableSteps: null, Planner);

            // Before resolution: resolver card present, no Lamco card.
            Assert.IsNotNull(vm.FindPhase(DeployPageViewModel.SubAutoRdpResolve));
            Assert.IsNull(vm.FindPhase(DeployPageViewModel.SubInstallLamcoRdp));
            Assert.IsNotNull(vm.FindPhase(PostBootStepPlanner.CardId(Steps.First(s => s.Name == "Force X11 Display Server"))),
                "under Auto (unresolved) the xrdp block is applicable (gate: != Lamco) — its cards are present");

            // The resolver ran: Auto → Lamco. Mirror the real protocol: the
            // presenter ACTIVATED the resolver's card when it reported, so at
            // reconcile time the card is Active (kept); only Pending cards
            // whose step turned inapplicable are removed.
            vm.ActivatePhase(DeployPageViewModel.SubAutoRdpResolve);
            wizard.Customizations.RdpBackend = RdpBackend.Lamco;
            vm.ReconcilePostBootSteps();

            Assert.IsNotNull(vm.FindPhase(DeployPageViewModel.SubInstallLamcoRdp),
                "the Lamco install card must be backfilled after resolution");
            Assert.IsNotNull(vm.FindPhase(DeployPageViewModel.SubEnableAutologin));
            Assert.IsNotNull(vm.FindPhase(DeployPageViewModel.SubStartGraphicalSession));
            Assert.IsNull(vm.FindPhase(PostBootStepPlanner.CardId(Steps.First(s => s.Name == "Force X11 Display Server"))),
                "the pending xrdp-block cards must be removed once the backend resolves to Lamco");

            // POSITION, not just presence: the backfilled Lamco cards sit in
            // plan order — directly after the resolver card (the last still-
            // applicable card before them) — and every remaining post-boot
            // card keeps its planner order around the KVP gate.
            var cardIds = vm.Phases
                .SkipWhile(p => p.Id != DeployPageViewModel.PhasePostBoot)
                .Skip(1)
                .TakeWhile(p => p.IndentLevel > 0)
                .Select(p => p.Id)
                .ToList();

            Assert.IsTrue(cardIds.IndexOf(DeployPageViewModel.SubAutoRdpResolve) >= 0, "resolver card still present");
            Assert.AreEqual(
                cardIds.IndexOf(DeployPageViewModel.SubAutoRdpResolve) + 1,
                cardIds.IndexOf(DeployPageViewModel.SubInstallLamcoRdp),
                "the backfilled Lamco install card must sit directly after the resolver card");
            Assert.IsTrue(
                cardIds.IndexOf(DeployPageViewModel.SubInstallLamcoRdp) < cardIds.IndexOf(DeployPageViewModel.SubEnableAutologin)
                && cardIds.IndexOf(DeployPageViewModel.SubEnableAutologin) < cardIds.IndexOf(DeployPageViewModel.SubStartGraphicalSession),
                "the Lamco branch cards must keep their plan order (install → autologin → session)");
            Assert.IsTrue(
                cardIds.IndexOf(DeployPageViewModel.SubStartGraphicalSession) < cardIds.IndexOf(DeployPageViewModel.SubWaitForKvpHeartbeat),
                "the Lamco branch cards must stay below the KVP gate card");
        }

        [TestMethod]
        public void Reconcile_NeverTouchesCompletedOrActiveCards()
        {
            var wizard = NewWizard(KaliDisk(), new VmCustomizations { RdpBackend = RdpBackend.Auto });
            var vm = new DeployPageViewModel(wizard, NullLogger.Instance, configurableSteps: null, Planner);

            var resolverCard = vm.FindPhase(DeployPageViewModel.SubAutoRdpResolve);
            Assert.IsNotNull(resolverCard);
            vm.ActivatePhase(DeployPageViewModel.SubAutoRdpResolve);
            vm.CompletePhase(DeployPageViewModel.SubAutoRdpResolve);

            wizard.Customizations.RdpBackend = RdpBackend.Lamco;
            vm.ReconcilePostBootSteps();

            Assert.AreEqual(DeploymentPhaseStatus.Completed, resolverCard!.Status,
                "a completed card is history — reconciliation must not touch it");
        }

        /// <summary>
        /// The core invariant: the post-boot card IDs, in list order, equal
        /// [prelude] + planner.CardId sequence (split around the KVP gate) —
        /// nothing more, nothing less, in no other order.
        /// </summary>
        private static void AssertCardOrderMatchesPlanner(
            DeployPageViewModel vm,
            WizardData wizard,
            StepPlatform platform,
            bool expectTempNic,
            bool expectKvp)
        {
            var postBootCards = vm.Phases
                .SkipWhile(p => p.Id != DeployPageViewModel.PhasePostBoot)
                .Skip(1)
                .TakeWhile(p => p.IndentLevel > 0)
                .Select(p => p.Id)
                .ToList();

            var planned = Planner.GetPlannedSteps(platform, wizard.SelectedItem, wizard.Customizations);
            var expected = new List<string>();
            if (expectTempNic) expected.Add(DeployPageViewModel.SubAddTempNic);
            expected.Add(DeployPageViewModel.SubWaitForSsh);
            expected.AddRange(planned
                .Where(s => s.Order < PostBootStepPlanner.SshRestoreOrder)
                .Select(PostBootStepPlanner.CardId));
            if (expectKvp) expected.Add(DeployPageViewModel.SubWaitForKvpHeartbeat);
            expected.AddRange(planned
                .Where(s => s.Order >= PostBootStepPlanner.SshRestoreOrder)
                .Select(PostBootStepPlanner.CardId));

            CollectionAssert.AreEqual(expected, postBootCards,
                "post-boot cards must be exactly [prelude] + planned(<900) + [kvp] + planned(>=900), in order");
        }

        private static WizardData NewWizard(GalleryItem item, VmCustomizations customizations) => new()
        {
            SelectedItem = item,
            Customizations = customizations,
        };

        [TestMethod]
        public void Present_RealisticAutoReportSequence_ActivatesCardsStrictlyDownward()
        {
            // The plan's regression guard against the reported bug: feeding the
            // presenter the exact report sequence the service emits must (a)
            // activate each card strictly further down the list than the one
            // before it and (b) never map a report to a missing card.
            //
            // The sequence is synthesized by walking the planner exactly as
            // PostBootCustomizationService does — live IsApplicable, the
            // resolver's in-place Auto→Lamco flip applied at its position in
            // the sequence — so the GUI sees precisely what a real
            // Auto-resolution deployment reports.
            var customizations = new VmCustomizations
            {
                RdpBackend = RdpBackend.Auto,
                SyncTimezone = true,
                EnableIntegrationServices = true,
            };
            var wizard = NewWizard(KaliDisk(), customizations);
            var vm = new DeployPageViewModel(wizard, NullLogger.Instance, configurableSteps: null, Planner);
            var presenter = new DeploymentProgressPresenter(
                new DeploymentProgressViewModelAdapter(vm),
                new SynchronousDispatcher(),
                wizard.SelectedItem!,
                customizations,
                Steps.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase),
                NullLogger.Instance);

            presenter.Present(new CreateVMProgressInfo { Phase = VmDeploymentPhase.PostBoot });

            int lastIndex = -1;
            void ReportAndCheck(CreateVMProgressInfo info)
            {
                presenter.Present(info);

                if (info.StepName == null) return;

                // No report may ever map to a missing card.
                var activeCard = vm.FindPhase(presenter.ActiveSubStepId!);
                Assert.IsNotNull(activeCard,
                    $"report '{info.StepName}' activated '{presenter.ActiveSubStepId}', which has no card");
                Assert.AreEqual(DeploymentPhaseStatus.Active, activeCard!.Status,
                    $"the card activated by '{info.StepName}' must actually be active");

                int index = vm.Phases.IndexOf(activeCard);
                Assert.IsTrue(index > lastIndex,
                    $"card '{presenter.ActiveSubStepId}' (from '{info.StepName}') must move strictly down the list; " +
                    $"previous index {lastIndex}, this index {index}");
                lastIndex = index;
            }

            // Prelude, exactly as the strategy reports it.
            ReportAndCheck(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.PostBoot, VmDeploymentSubStep.AddTempNic));
            ReportAndCheck(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.PostBoot, VmDeploymentSubStep.WaitForSsh));

            // Step reports in service order. The resolver flips Auto→Lamco mid
            // sequence (mirroring AutoRdpBackendResolveStep's in-place
            // mutation) so the live re-plan is exercised end to end.
            foreach (var step in Planner.GetOrderedCandidates(StepPlatform.Linux))
            {
                if (step.Order >= PostBootStepPlanner.SshRestoreOrder) break;

                if (!step.IsApplicable(wizard.SelectedItem, customizations)) continue;
                if (step.Name == "Auto-select RDP backend")
                {
                    ReportAndCheck(new CreateVMProgressInfo
                    {
                        Phase = VmDeploymentPhase.PostBoot,
                        StepName = step.Name
                    });
                    customizations.RdpBackend = RdpBackend.Lamco; // resolver's in-place mutation
                    continue;
                }

                ReportAndCheck(new CreateVMProgressInfo
                {
                    Phase = VmDeploymentPhase.PostBoot,
                    StepName = step.Name
                });
            }

            // The KVP gate sits between the two halves, exactly as the strategy reports it.
            ReportAndCheck(CreateVMProgressInfo.ForPhase(VmDeploymentPhase.PostBoot, VmDeploymentSubStep.WaitForKvpHeartbeat));

            // The restore tail.
            foreach (var step in Planner.GetOrderedCandidates(StepPlatform.Linux))
            {
                if (step.Order < PostBootStepPlanner.SshRestoreOrder) continue;
                if (!step.IsApplicable(wizard.SelectedItem, customizations)) continue;

                ReportAndCheck(new CreateVMProgressInfo
                {
                    Phase = VmDeploymentPhase.PostBoot,
                    StepName = step.Name
                });
            }

            presenter.CompleteActive();
        }

        [TestMethod]
        public void ChrootCapableSteps_GetCustomizeCards_AndVanishFromPostBoot_IsoCycle()
        {
            // A chroot-capable planned step runs inside the ISO stage on the
            // disk-image ISO-cycle flow: its card must live in the Customize
            // phase (before SshSetup) and must NOT appear in the post-boot
            // card list (the backend ledger skips it there).
            var chrootStep = new FakeStep("Chropy Step", 100, applicable: true, supportsChroot: true);
            var planner = new PostBootStepPlanner(new ICustomizationStep[] { chrootStep });
            var wizard = NewWizard(KaliDisk(), new VmCustomizations { SyncTimezone = false });

            var vm = new DeployPageViewModel(wizard, NullLogger.Instance, configurableSteps: null, planner);

            var customizeCards = vm.Phases
                .SkipWhile(p => p.Id != DeployPageViewModel.PhaseCustomize)
                .Skip(1)
                .TakeWhile(p => p.IndentLevel > 0)
                .Select(p => p.Id)
                .ToList();
            CollectionAssert.Contains(customizeCards, PostBootStepPlanner.CardId(chrootStep),
                "the chroot step's card must exist in the Customize phase");
            Assert.IsTrue(customizeCards.IndexOf(PostBootStepPlanner.CardId(chrootStep))
                          < customizeCards.IndexOf(DeployPageViewModel.SubSshSetup),
                "chroot cards run before the automation-user setup");

            var postBootCards = vm.Phases
                .SkipWhile(p => p.Id != DeployPageViewModel.PhasePostBoot)
                .Skip(1)
                .TakeWhile(p => p.IndentLevel > 0)
                .Select(p => p.Id)
                .ToList();
            CollectionAssert.DoesNotContain(postBootCards, PostBootStepPlanner.CardId(chrootStep),
                "a step that ran in the chroot must not get a post-boot card");
        }

        [TestMethod]
        public void ChrootCapableSteps_StayPostBoot_CloudInitFlow()
        {
            // Same step, cloud-init flow: no ISO stage exists, so the step
            // runs post-boot and its card stays in the PostBoot phase.
            var chrootStep = new FakeStep("Chropy Step", 100, applicable: true, supportsChroot: true);
            var planner = new PostBootStepPlanner(new ICustomizationStep[] { chrootStep });
            var wizard = NewWizard(new GalleryItem
            {
                Name = "Elastic SIEM",
                DiskUri = "http://example.com/elastic.vhdx",
                LinuxDistro = LinuxDistro.Unknown,
                Tags = { "cloud-init" },
            }, new VmCustomizations());

            var vm = new DeployPageViewModel(wizard, NullLogger.Instance, configurableSteps: null, planner);

            Assert.IsNull(vm.FindPhase(DeployPageViewModel.PhaseCustomize),
                "no Customize phase on the cloud-init flow");
            Assert.IsNotNull(vm.FindPhase(PostBootStepPlanner.CardId(chrootStep)),
                "the chroot-capable step keeps its post-boot card (fallback)");
        }

        private sealed class FakeStep : ICustomizationStep
        {
            public FakeStep(string name, int order, bool applicable, bool supportsChroot)
            {
                Name = name;
                Order = order;
                _applicable = applicable;
                SupportsChroot = supportsChroot;
            }

            private readonly bool _applicable;
            public string Name { get; }
            public CustomizationPhase Phase => CustomizationPhase.PostBoot;
            public StepPlatform Platform => StepPlatform.Linux;
            public int Order { get; }
            public string? ProgressPhaseId => "Sub_" + Name.Replace(' ', '_');
            bool ICustomizationStep.SupportsChroot => SupportsChroot;
            private bool SupportsChroot { get; }
            public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations) => _applicable;
            public Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations,
                Microsoft.Extensions.Logging.ILogger logger, CancellationToken ct)
                => Task.CompletedTask;
        }

        private sealed class SynchronousDispatcher : IDispatcher
        {
            public void Invoke(Action action) => action();

            public Task InvokeAsync(Action action)
            {
                action();
                return Task.CompletedTask;
            }
        }

        private static List<ICustomizationStep> DiscoverSteps()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddLogging();
            services.AddHttpClient();
            var assemblies = new[]
            {
                typeof(SyncTimezoneStep).Assembly,        // VMCreate.Core
                typeof(FlareVm).Assembly                  // VMCreate.Gallery.Security
            };
            var stepTypes = assemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => typeof(ICustomizationStep).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
            foreach (var t in stepTypes)
                services.AddTransient(typeof(ICustomizationStep), t);

            return services.BuildServiceProvider().GetServices<ICustomizationStep>().ToList();
        }
    }
}