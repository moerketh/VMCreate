using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using VMCreate;
using VMCreate.Gallery;
using VMCreate.HyperV.VmCreation;

namespace VMCreate.Tests.HyperV.VmCreation
{
    /// <summary>
    /// Resolves the REAL service container end to end. The regression this
    /// pins: <c>PostBootCustomizationService</c> briefly shipped with two
    /// public constructors whose parameter sets were both DI-resolvable,
    /// and Microsoft.Extensions.DependencyInjection rejects that only at
    /// ACTIVATION time — i.e. when the user starts a deployment — with
    /// "The following constructors are ambiguous". Nothing in CI caught it
    /// because the app container is only resolved by the opt-in GUI smoke
    /// tests. This suite builds the same container
    /// (<see cref="ServiceCollectionExtensions.AddCoreServices"/> with the
    /// canonical scannable assemblies) and eagerly activates every
    /// registered service, so any constructor ambiguity, missing
    /// registration or captive dependency fails HERE instead of at
    /// deployment start.
    /// </summary>
    [TestClass]
    public class ServiceContainerActivationTests
    {
        private static (ServiceProvider Provider, ServiceCollection Services) BuildContainer()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpClient();
            services.AddCoreServices(
                typeof(SyncTimezoneStep).Assembly,   // VMCreate.Core
                typeof(BlackArch).Assembly);         // VMCreate.Gallery.Security
            return (services.BuildServiceProvider(), services);
        }

        [TestMethod]
        public void EveryRegisteredService_ActivatesWithoutAmbiguity()
        {
            var (provider, services) = BuildContainer();
            using var __ = provider;

            var failures = new System.Text.StringBuilder();
            foreach (ServiceDescriptor descriptor in services)
            {
                // Open-generic registrations (IOptions<>, ILogger<>, …) can
                // only be resolved against a CLOSED generic type — skipping
                // them is not a coverage loss: our registrations are closed.
                if (descriptor.ServiceType.IsGenericType
                    && descriptor.ServiceType.IsGenericTypeDefinition)
                    continue;

                try
                {
                    _ = provider.GetRequiredService(descriptor.ServiceType);
                }
                catch (Exception ex)
                {
                    failures.AppendLine($"{descriptor.ServiceType.Name}: {ex.Message}");
                }
            }

            Assert.AreEqual(string.Empty, failures.ToString(),
                "every registered service must activate — a failure here ships as a " +
                "deployment-time crash (the ambiguous-constructor bug class):\n" + failures);
        }

        [TestMethod]
        public void PostBootCustomizationService_HasExactlyOnePublicConstructor()
        {
            // Direct pin on the original culprit: two public constructors
            // with resolvable parameter sets are the ambiguity trap. If a
            // convenience overload is ever needed again, use a static factory
            // or [ActivatorUtilitiesConstructor] instead.
            var ctors = typeof(PostBootCustomizationService).GetConstructors();
            Assert.AreEqual(1, ctors.Length,
                "PostBootCustomizationService must keep exactly one public constructor — " +
                "MEDI resolves multiple resolvable constructors ambiguously and fails at " +
                "deployment start, not at startup.");
        }

        [TestMethod]
        public void PostBootCustomizationService_ResolvesAsThePlannerBasedService()
        {
            var (provider, _) = BuildContainer();
            using var _ = provider;
            var service = provider.GetRequiredService<IPostBootCustomizationService>();
            Assert.IsInstanceOfType(service, typeof(PostBootCustomizationService));

            // And the planner it depends on is the DI singleton — the same
            // instance the GUI's card builder uses.
            var planner = provider.GetRequiredService<IPostBootStepPlanner>();
            Assert.IsNotNull(planner, "the planner is registered and resolvable");
        }
    }
}
