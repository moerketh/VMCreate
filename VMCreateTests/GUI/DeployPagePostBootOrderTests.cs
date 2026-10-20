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

            // The resolver ran: Auto → Lamco.
            wizard.Customizations.RdpBackend = RdpBackend.Lamco;
            vm.ReconcilePostBootSteps();

            Assert.IsNotNull(vm.FindPhase(DeployPageViewModel.SubInstallLamcoRdp),
                "the Lamco install card must be backfilled after resolution");
            Assert.IsNotNull(vm.FindPhase(DeployPageViewModel.SubEnableAutologin));
            Assert.IsNotNull(vm.FindPhase(DeployPageViewModel.SubStartGraphicalSession));
            Assert.IsNull(vm.FindPhase(PostBootStepPlanner.CardId(Steps.First(s => s.Name == "Force X11 Display Server"))),
                "the pending xrdp-block cards must be removed once the backend resolves to Lamco");
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