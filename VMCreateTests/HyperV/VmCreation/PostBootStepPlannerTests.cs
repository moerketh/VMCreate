using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using VMCreate;
using VMCreate.Gallery;
using VMCreate.HyperV.VmCreation;

namespace VMCreate.Tests.HyperV.VmCreation
{
    /// <summary>
    /// Pins the deploy page's post-boot card list to the backend's execution
    /// order. Both come from <see cref="PostBootStepPlanner"/> — this suite
    /// fails the moment either side drifts (a new step without a card, a
    /// re-ordered list, a card whose step never runs).
    /// </summary>
    [TestClass]
    public class PostBootStepPlannerTests
    {
        private static ICustomizationStep Step(string name, int order, bool applicable = true,
            string? progressPhaseId = null, StepPlatform platform = StepPlatform.Linux,
            CustomizationPhase phase = CustomizationPhase.PostBoot)
            => new FakeStep(name, order, applicable, progressPhaseId, platform, phase);

        [TestMethod]
        public void GetOrderedCandidates_SortsByOrder_ThenNameOrdinal()
        {
            var planner = new PostBootStepPlanner(new[]
            {
                Step("Beta", 100),
                Step("Alpha", 200),
                Step("gamma", 100),  // same Order as Beta — Name decides
                Step("Out", 50),
                Step("Skip", 100, applicable: false), // ordering ignores applicability
                Step("Win", 100, platform: StepPlatform.Windows),
                Step("PreBoot", 100, phase: CustomizationPhase.PreBoot),
            });

            var names = planner.GetOrderedCandidates(StepPlatform.Linux).Select(s => s.Name).ToArray();

            // "Skip" (S=83) sorts before "gamma" (g=103) under ordinal
            // comparison — the tie-break must be ordinal, not case-insensitive.
            CollectionAssert.AreEqual(new[] { "Out", "Beta", "Skip", "gamma", "Alpha" }, names,
                "ordered by Order first, then by Name (ordinal); platform and phase filtered; applicability ignored");
        }

        [TestMethod]
        public void GetPlannedSteps_FiltersApplicable_KeepingOrder()
        {
            var planner = new PostBootStepPlanner(new[]
            {
                Step("Keep2", 200),
                Step("Drop1", 100, applicable: false),
                Step("Keep1", 100),
                Step("Drop2", 300, applicable: false),
            });

            var names = planner.GetPlannedSteps(StepPlatform.Linux, null, null).Select(s => s.Name).ToArray();

            CollectionAssert.AreEqual(new[] { "Keep1", "Keep2" }, names,
                "only applicable candidates are planned, in Order-then-Name order");
        }

        [TestMethod]
        public void CardId_UsesProgressPhaseId_WhenSet_FallsBackToName()
        {
            var withId = Step("Sync Timezone", 100, progressPhaseId: "Sub_SyncTimezone");
            var withoutId = Step("Remove Automation User", 850);

            Assert.AreEqual("Sub_SyncTimezone", PostBootStepPlanner.CardId(withId));
            Assert.AreEqual("Sub_Step_Remove Automation User", PostBootStepPlanner.CardId(withoutId));
        }

        [TestMethod]
        public void SshRestoreOrder_IsTheDisableSshOrder()
        {
            // The strategy splits the pipeline at this boundary and the GUI
            // splits the card list at the same one — pin it to the actual
            // DisableSshStep value so the two can never diverge silently.
            Assert.AreEqual(new DisableSshStep().Order, PostBootStepPlanner.SshRestoreOrder);
        }

        [TestMethod]
        public void ServiceAndPlanner_ProduceIdenticalOrdering_OverTheRealStepSet()
        {
            // Discover exactly as the app does (assembly scan + DI), then
            // verify the service's executor ordering and the planner's
            // candidate ordering agree for both platforms.
            var coreSteps = DiscoverSteps();

            var planner = new PostBootStepPlanner(coreSteps);

            foreach (var platform in new[] { StepPlatform.Linux, StepPlatform.Windows })
            {
                var plannerOrder = planner.GetOrderedCandidates(platform).Select(s => s.Name).ToArray();
                var executorOrder = coreSteps
                    .Where(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == platform)
                    .OrderBy(s => s.Order)
                    .ThenBy(s => s.Name, StringComparer.Ordinal)
                    .Select(s => s.Name)
                    .ToArray();

                CollectionAssert.AreEqual(executorOrder, plannerOrder,
                    $"the planner and the executor must agree on {platform} post-boot ordering");
            }
        }

        [TestMethod]
        public void EveryExecutedPostBootStep_MapsToAUniqueCardId()
        {
            // The core invariant behind "no gaps": the presenter maps a step
            // report to CardId(step), so every candidate must produce a
            // unique, non-empty ID — two steps sharing a card ID (duplicate
            // ProgressPhaseId) would overwrite each other's status.
            var coreSteps = DiscoverSteps();

            var ids = coreSteps
                .Where(s => s.Phase == CustomizationPhase.PostBoot)
                .Select(PostBootStepPlanner.CardId)
                .ToList();

            foreach (var id in ids)
                Assert.IsFalse(string.IsNullOrWhiteSpace(id), "CardId must never be empty");

            var duplicates = ids.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
            Assert.AreEqual(0, duplicates.Count,
                "duplicate card IDs: " + string.Join(", ", duplicates.Select(g => g.Key)));
        }

        /// <summary>
        /// Discovers and instantiates every <see cref="ICustomizationStep"/> in
        /// the two assemblies the app scans, exactly as the app does at startup
        /// (same mechanism as <c>CustomizationStepPlatformTests</c>).
        /// </summary>
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

        private sealed class FakeStep : ICustomizationStep
        {
            public FakeStep(string name, int order, bool applicable, string? progressPhaseId,
                StepPlatform platform, CustomizationPhase phase)
            {
                Name = name;
                Order = order;
                _applicable = applicable;
                ProgressPhaseId = progressPhaseId;
                Platform = platform;
                Phase = phase;
            }

            private readonly bool _applicable;
            public string Name { get; }
            public CustomizationPhase Phase { get; }
            public StepPlatform Platform { get; }
            public int Order { get; }
            public string? ProgressPhaseId { get; }
            public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations) => _applicable;
            public Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations,
                Microsoft.Extensions.Logging.ILogger logger, System.Threading.CancellationToken ct)
                => Task.CompletedTask;
        }
    }
}