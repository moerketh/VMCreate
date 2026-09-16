using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VMCreate;
using VMCreate.Gallery;

namespace VMCreate.Tests
{
    /// <summary>
    /// Pins the CLI-vs-GUI front-end parity introduced when the CLI's
    /// step registration was fixed. <see cref="VMCreate.CLI.Program"/> used
    /// to scan the CLI assembly itself (instead of the shared deployment
    /// assembly, where every <see cref="ICustomizationStep"/> lives), so
    /// CLI deployments registered ZERO steps: no post-boot work at all —
    /// and with RDP backend Auto, not even xrdp. That shipped green once;
    /// these tests make a recurrence loud instead of silent.
    /// <para>
    /// Both front ends scan the same two assemblies (named explicitly since
    /// each has its own assembly): VMCreate.Core — where the Core/GUI split
    /// moved every step and general gallery loader, pinned here through
    /// <see cref="App"/>.xaml.cs's <c>AddCoreServices(typeof(SyncTimezoneStep)
    /// .Assembly, …)</c> set — plus Gallery.Security. Every assertion here
    /// compares the two front ends' discovery sets, so a drift on EITHER
    /// side fails.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class CliFrontEndParityTests
    {
        /// <summary>The GUI's scan set, exactly as App.xaml.cs defines it.</summary>
        private static readonly Assembly[] GuiAssemblies =
        {
            typeof(SyncTimezoneStep).Assembly,            // VMCreate.Core — steps + general gallery
            typeof(FlareVm).Assembly                      // VMCreate.Gallery.Security
        };

        /// <summary>The CLI's scan set (single source of truth used by Program.Main).</summary>
        private static readonly System.Reflection.Assembly[] CliAssemblies = global::VMCreate.CLI.Program.ScannableAssemblies;

        private static HashSet<Type> DiscoverStepTypes(IEnumerable<Assembly> assemblies) =>
            new(assemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => typeof(ICustomizationStep).IsAssignableFrom(t)
                            && !t.IsAbstract
                            && !t.IsInterface));

        private static HashSet<Type> DiscoverGalleryLoaderTypes(IEnumerable<Assembly> assemblies) =>
            new(assemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => typeof(IGalleryLoader).IsAssignableFrom(t)
                            && !t.IsAbstract
                            && !t.IsInterface
                            && t != typeof(AggregateGalleryLoader)));

        [TestMethod]
        public void BothFrontEnds_DiscoverTheSameCustomizationSteps()
        {
            var guiSteps = DiscoverStepTypes(GuiAssemblies);
            var cliSteps = DiscoverStepTypes(CliAssemblies);

            var missingInCli = guiSteps.Except(cliSteps).ToList();
            var missingInGui = cliSteps.Except(guiSteps).ToList();

            Assert.AreEqual(0, missingInCli.Count,
                $"Steps the GUI registers but the CLI does not (CLI deployments silently skip these): {string.Join(", ", missingInCli.Select(t => t.Name))}");
            Assert.AreEqual(0, missingInGui.Count,
                $"Steps the CLI registers but the GUI does not: {string.Join(", ", missingInGui.Select(t => t.Name))}");
        }

        [TestMethod]
        public void BothFrontEnds_DiscoverTheSameGalleryLoaders()
        {
            var guiLoaders = DiscoverGalleryLoaderTypes(GuiAssemblies);
            var cliLoaders = DiscoverGalleryLoaderTypes(CliAssemblies);

            var missingInCli = guiLoaders.Except(cliLoaders).ToList();
            Assert.AreEqual(0, missingInCli.Count,
                $"Gallery loaders the GUI registers but the CLI does not (CLI gallery silently missing these distros): {string.Join(", ", missingInCli.Select(t => t.Name))}");
        }

        [TestMethod]
        public void CliDiscovers_TheReleaseCriticalLinuxSteps()
        {
            // The RDP-backend Auto pipeline: if any of these three is
            // invisible to the CLI, 'auto' (the DEFAULT) resolves to no
            // RDP at all — the original release-blocker.
            var cliSteps = DiscoverStepTypes(CliAssemblies);

            Assert.IsTrue(cliSteps.Contains(typeof(AutoRdpBackendResolveStep)),
                "CLI step discovery is missing AutoRdpBackendResolveStep — RDP Auto would never resolve to a backend.");
            Assert.IsTrue(cliSteps.Contains(typeof(InstallXrdpPostBootStep)),
                "CLI step discovery is missing InstallXrdpPostBootStep — the post-boot xrdp backfill would never run.");
            Assert.IsTrue(cliSteps.Contains(typeof(KaliKdeSwitchStep)),
                "CLI step discovery is missing KaliKdeSwitchStep — the Kali KDE desktop switch would never run.");
        }

        [TestMethod]
        public void CliDiscovers_GreaterThanTwentyLinuxPostBootSteps()
        {
            // Floor guard, not an exact count: the Linux post-boot set
            // (27 steps at the time of the fix). Catches "scanned the
            // wrong assembly again" (which yields ~7 Gallery.Security steps
            // or zero) while tolerating normal step churn.
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddLogging();
            services.AddHttpClient();
            foreach (var t in DiscoverStepTypes(CliAssemblies))
                services.AddTransient(typeof(ICustomizationStep), t);

            var steps = services.BuildServiceProvider()
                .GetServices<ICustomizationStep>();

            int linuxPostBootCount = steps
                .Count(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == StepPlatform.Linux);

            Assert.IsTrue(linuxPostBootCount >= 20,
                $"CLI step discovery found only {linuxPostBootCount} Linux PostBoot steps " +
                "(expected >= 20) — the scan is probably pointed at the wrong assembly again.");
        }

        [TestMethod]
        public void CliAssemblyItself_DefinesNoStepsOrLoaders()
        {
            // The invariant that lets both front ends skip the CLI assembly:
            // it holds only UI/command plumbing. If a step is added to
            // VMCreate.CLI, it must MOVE to VMCreate.Core — the GUI would
            // otherwise never see it (and neither front end scans the CLI
            // assembly).
            Assembly cliAssembly = typeof(global::VMCreate.CLI.Program).Assembly;

            var steps = cliAssembly.GetTypes()
                .Where(t => typeof(ICustomizationStep).IsAssignableFrom(t)
                            && !t.IsAbstract
                            && !t.IsInterface)
                .ToList();
            Assert.AreEqual(0, steps.Count,
                $"Steps defined in the CLI assembly: {string.Join(", ", steps.Select(t => t.FullName))} — "
                + "move them into VMCreate.Core (VMCreate.Core.csproj) where both front ends discover them.");

            var loaders = cliAssembly.GetTypes()
                .Where(t => typeof(IGalleryLoader).IsAssignableFrom(t)
                            && !t.IsAbstract
                            && !t.IsInterface)
                .ToList();
            Assert.AreEqual(0, loaders.Count,
                $"Gallery loaders defined in the CLI assembly: {string.Join(", ", loaders.Select(t => t.FullName))} — "
                + "move them into VMCreate.Core where both front ends discover them.");
        }
    }
}