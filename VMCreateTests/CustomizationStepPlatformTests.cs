using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMCreate;
using VMCreate.Gallery;

namespace VMCreate.Tests
{
    /// <summary>
    /// Guards the Linux/Windows separation of customization steps. Both the SSH (Linux) and
    /// PowerShell Direct (Windows) post-boot flows select steps by <see cref="StepPlatform"/>;
    /// if a Linux step (e.g. one that runs <c>sudo</c>) were ever selected for a Windows VM it
    /// would fail at runtime. These tests fail fast if a step is mis-tagged.
    /// </summary>
    [TestClass]
    public class CustomizationStepPlatformTests
    {
        /// <summary>
        /// Discovers and instantiates every <see cref="ICustomizationStep"/> exactly as the app
        /// does at startup (assembly scan + DI), so the test set always matches production.
        /// </summary>
        private static List<ICustomizationStep> DiscoverSteps()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpClient(); // provides IHttpClientFactory for steps that download tooling

            // Same two assemblies App.xaml.cs scans: the main app and the security gallery.
            var assemblies = new[] { typeof(SyncTimezoneStep).Assembly, typeof(FlareVm).Assembly };
            var stepTypes = assemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => typeof(ICustomizationStep).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
            foreach (var t in stepTypes)
                services.AddTransient(typeof(ICustomizationStep), t);

            return services.BuildServiceProvider().GetServices<ICustomizationStep>().ToList();
        }

        [TestMethod]
        public void EveryStep_DeclaresAKnownPlatform()
        {
            foreach (var step in DiscoverSteps())
                Assert.IsTrue(
                    step.Platform == StepPlatform.Windows || step.Platform == StepPlatform.Linux,
                    $"{step.GetType().Name} does not declare a valid StepPlatform");
        }

        [TestMethod]
        public void VBoxGuestAdditionsStep_IsLinuxOnly()
        {
            // Regression guard: this Linux step runs `sudo` and previously ran on the Windows
            // FLARE VM over PowerShell Direct, failing with "'sudo' is not recognized".
            var step = DiscoverSteps().Single(s => s is RemoveVBoxGuestAdditionsStep);
            Assert.AreEqual(StepPlatform.Linux, step.Platform);
        }

        [TestMethod]
        public void FlareSteps_AreWindowsOnly()
        {
            var flareSteps = DiscoverSteps()
                .Where(s => s.GetType().Name.StartsWith("FlareVm", StringComparison.Ordinal))
                .ToList();

            Assert.IsTrue(flareSteps.Count > 0, "expected the FLARE VM steps to be discovered");
            foreach (var step in flareSteps)
                Assert.AreEqual(StepPlatform.Windows, step.Platform,
                    $"{step.GetType().Name} should target Windows");
        }

        [TestMethod]
        public void PlatformGate_PartitionsAllSteps_WithBothPlatformsPopulated()
        {
            var steps = DiscoverSteps();
            var windowsSteps = steps.Where(s => s.Platform == StepPlatform.Windows).ToList();
            var linuxSteps = steps.Where(s => s.Platform == StepPlatform.Linux).ToList();

            // The orchestrator selects strictly by platform, so the two sets must be a total,
            // disjoint partition — no step is ever eligible for both transports.
            Assert.AreEqual(steps.Count, windowsSteps.Count + linuxSteps.Count,
                "every step must belong to exactly one platform");
            Assert.IsTrue(windowsSteps.Count > 0, "expected at least one Windows step");
            Assert.IsTrue(linuxSteps.Count > 0, "expected at least one Linux step");
        }
    }
}
