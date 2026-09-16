using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CreateVM.HyperV.vmbus;
using System;
using System.Linq;
using System.Reflection;
using VMCreate.Gallery;
using VMCreate.HyperV.Unattend;
using VMCreate.HyperV.VmCreation;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    /// <summary>
    /// Shared DI registrations for the non-UI deployment engine — the single
    /// source of truth both front ends (GUI App.xaml.cs and the CLI Program.Main)
    /// call so the two containers can never drift. Before the Core/GUI split
    /// these blocks were duplicated verbatim in <c>App.xaml.cs</c> and
    /// <c>Program.cs</c> (<c>CliFrontEndParityTests</c> pins them equivalent);
    /// now the parity is structural.
    /// <para>
    /// Callers keep ownership of the front-end-specific parts: Serilog
    /// <c>Log.Logger</c>, <c>AddLogging(ClearProviders + AddSerilog)</c>,
    /// <c>AddHttpClient()</c> (the typed-client <c>AddHttpClient&lt;IHtbApiClient,
    /// HtbApiClient&gt;</c> inside is part of the core set), and everything
    /// UI-sided (in the GUI: the DeployPage factory and the name→step lookup
    /// it consumes; nothing extra in the CLI).
    /// </para>
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the core deployment services using the canonical GUI order.
        /// <paramref name="scannableAssemblies"/> are the assemblies scanned for
        /// auto-discovered <see cref="IGalleryLoader"/> and
        /// <see cref="ICustomizationStep"/> implementations — the same set for
        /// both front ends (pinned by <c>CliFrontEndParityTests</c>): the
        /// VMCreate.Core assembly (steps + general gallery) and
        /// VMCreate.Gallery.Security. Callers pass them explicitly via
        /// <c>typeof(SyncTimezoneStep).Assembly</c> and
        /// <c>typeof(BlackArch).Assembly</c> so a wrong-assembly scan fails the
        /// parity tests instead of silently registering zero steps.
        /// </summary>
        public static IServiceCollection AddCoreServices(
            this IServiceCollection services,
            params Assembly[] scannableAssemblies)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));

            // ── Configuration ───────────────────────────────────────────────
            services.AddSingleton(Options.Create(new AppSettings()));

            // ── Infrastructure / low-level services ─────────────────────────
            services.AddTransient<IFileStreamProvider, FileStreamProvider>();
            services.AddTransient<IHttpStreamProvider, HttpStreamProvider>();
            services.AddTransient<IStreamCopierWithProgress, StreamCopierWithProgress>();
            services.AddTransient<IDownloader, HttpFileDownloader>();
            services.AddTransient<IChecksumVerifier, ChecksumVerifier>();
            services.AddTransient<ICloningIsoDownloader, CloningIsoDownloader>();

            // ── Hyper-V / VM plumbing ───────────────────────────────────────
            // Fully-qualified because VMCreate.HyperV.Unattend defines its own
            // IPowerShellExecutor. Singleton, matching the original GUI
            // registration: InitialSessionState construction is the expensive
            // part of PowerShell hosting (~600 ms measured); a transient
            // registration would re-pay it on every Hyper-V cmdlet of a deploy run.
            services.AddSingleton<VMCreate.HyperV.IPowerShellExecutor, VMCreate.HyperV.PowerShellExecutor>();
            services.AddTransient<VMCreate.HyperV.Unattend.IPowerShellExecutor, VMCreate.HyperV.Unattend.PowerShellExecutor>();
            // Five focused role managers, then the IHyperVManager facade that
            // wraps them all.
            services.AddSingleton<IVmLifecycleManager, PowerShellVmLifecycleManager>();
            services.AddSingleton<IVmDiskManager, PowerShellVmDiskManager>();
            services.AddSingleton<IVmBootManager, PowerShellVmBootManager>();
            services.AddSingleton<IVmNetworkManager, PowerShellVmNetworkManager>();
            services.AddSingleton<IVmConfigManager, PowerShellVmConfigManager>();
            services.AddSingleton<IHyperVManager, PowerShellHyperVManagerFacade>();
            services.AddSingleton<IUnattendInjector, ElevatedUnattendInjector>();
            services.AddTransient<IOfflineRegistryEditor, OfflineRegistryEditor>();
            services.AddTransient<UnattendInjector>();
            services.AddSingleton<ISshKeyManager, SshKeyManager>();
            services.AddTransient<IKvpSender, KvpHostToGuest>();
            services.AddTransient<IKvpPoller, HyperVKVPPoller>();
            services.AddTransient<IVmShutdownWatcher, HyperVKVPPoller>();
            services.AddTransient<IGuestDiagnosticsCollector, GuestDiagnosticsCollector>();
            services.AddTransient<IGuestShellFactory, GuestShellFactory>();
            services.AddTransient<PowerShellDirectGuestShellFactory>();

            // ── VM creation services ────────────────────────────────────────
            services.AddSingleton<IVmPathService, VmPathService>();
            services.AddSingleton<IHostNetworkService, HostNetworkService>();
            services.AddTransient<IPostBootCustomizationService, PostBootCustomizationService>();
            services.AddTransient<IIsoBootCycleRunner, IsoBootCycleRunner>();
            services.AddTransient<IVmCreationStrategy, IsoVmCreationStrategy>();
            services.AddTransient<IVmCreationStrategy, NativeHyperVVmCreationStrategy>();
            services.AddTransient<IVmCreationStrategy, DiskImageVmCreationStrategy>();

            // ── Disk / media handling ───────────────────────────────────────
            services.AddSingleton<IDiskConverter, DiskConverter>();
            services.AddSingleton<IMediaHandlerFactory, MediaHandlerFactory>();
            services.AddTransient<XzFileExtractor>();
            services.AddTransient<ArchiveExtractor>();
            services.AddTransient<IExtractor>(provider => new ExtractorFactory(
                provider.GetRequiredService<XzFileExtractor>(),
                provider.GetRequiredService<ArchiveExtractor>(),
                provider.GetRequiredService<ILogger<ExtractorFactory>>()));
            services.AddTransient<DiskFileDetector>();

            // ── Gallery loaders (auto-discovered) ───────────────────────────
            var galleryLoaderTypes = scannableAssemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => typeof(IGalleryLoader).IsAssignableFrom(t)
                            && !t.IsAbstract
                            && !t.IsInterface
                            && t != typeof(AggregateGalleryLoader));
            foreach (var loaderType in galleryLoaderTypes)
                services.AddTransient(loaderType);

            services.AddTransient<IGalleryLoader>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<AggregateGalleryLoader>>();
                var loaders = galleryLoaderTypes.Select(t => (IGalleryLoader)provider.GetRequiredService(t));
                return new AggregateGalleryLoader(logger, loaders);
            });
            services.AddTransient<IGalleryItemsParser, GalleryItemsParser>();
            services.AddSingleton<IGalleryCache, GalleryCache>();
            services.AddTransient<IGalleryService, GalleryService>();

            // ── Customization steps (auto-discovered) ───────────────────────
            var stepTypes = scannableAssemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => typeof(ICustomizationStep).IsAssignableFrom(t)
                            && !t.IsAbstract
                            && !t.IsInterface);
            foreach (var stepType in stepTypes)
                services.AddTransient(typeof(ICustomizationStep), stepType);

            var configurableStepTypes = stepTypes
                .Where(t => typeof(IConfigurableCustomizationStep).IsAssignableFrom(t));
            foreach (var stepType in configurableStepTypes)
                services.AddTransient(typeof(IConfigurableCustomizationStep), stepType);

            // ── HTB API client (uses IHttpClientFactory) ────────────────────
            services.AddHttpClient<IHtbApiClient, HtbApiClient>();

            // ── VM creation orchestrator ────────────────────────────────────
            services.AddTransient<IVmDeploymentOrchestrator, VmDeploymentOrchestrator>();
            services.AddTransient<IVmCreator, HyperVVmCreator>();
            services.AddTransient<CreateVM>();
            services.AddSingleton<IPartitionSchemeDetector, PartitionSchemeDetector>();
            services.AddSingleton<IVmGenerationResolver, VmGenerationResolver>();

            return services;
        }
    }
}
