using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CreateVM.HyperV.vmbus;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VMCreate;
using VMCreate.Gallery;
using VMCreate.HyperV.Unattend;
using VMCreate.HyperV.VmCreation;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    public partial class App : Application
    {
        private IServiceProvider _serviceProvider;

        /// <summary>When true, VMConnect is launched automatically after the VM starts.</summary>
        internal static bool DemoMode { get; private set; }

        private async void App_OnStartup(object sender, StartupEventArgs e)
        {
            // ── Headless elevated child: --inject-unattend <vhdxPath> ────────
            // When the GUI spawns itself elevated via ElevatedUnattendInjector,
            // the child runs only the injection logic and exits — no UI is shown.
            if (e.Args != null && e.Args.Length >= 2 &&
                e.Args[0].Equals("--inject-unattend", StringComparison.OrdinalIgnoreCase))
            {
                string vhdxPath = e.Args[1];
                string injectLogPath = Path.Combine(Path.GetTempPath(), "VMCreate.inject.log");
                var injectSerilog = new Serilog.LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.File(injectLogPath, rollingInterval: RollingInterval.Day, shared: true)
                    .CreateLogger();

                var injectServices = new ServiceCollection();
                injectServices.AddLogging(b =>
                {
                    b.ClearProviders();
                    b.AddSerilog(injectSerilog, dispose: true);
                });
                injectServices.AddTransient<VMCreate.HyperV.Unattend.IPowerShellExecutor, VMCreate.HyperV.Unattend.PowerShellExecutor>();
                injectServices.AddTransient<IOfflineRegistryEditor, OfflineRegistryEditor>();
                injectServices.AddTransient<UnattendInjector>();
                var sp = injectServices.BuildServiceProvider();
                var injector = sp.GetRequiredService<UnattendInjector>();
                var injectLogger = sp.GetRequiredService<ILogger<UnattendInjector>>();

                try
                {
                    injectLogger.LogInformation("Elevated child: injecting unattend.xml into {VhdxPath}", vhdxPath);
                    bool ok = await injector.InjectAsync(vhdxPath, CancellationToken.None);
                    injectLogger.LogInformation("Injection result: {Result}", ok ? "succeeded" : "failed");
                    Environment.ExitCode = ok ? 0 : 1;
                }
                catch (Exception ex)
                {
                    injectLogger.LogError(ex, "Injection failed: {Message}", ex.Message);
                    Environment.ExitCode = 1;
                }
                finally
                {
                    (sp as IDisposable)?.Dispose();
                }

                Shutdown();
                return;
            }

            DemoMode = e.Args != null && e.Args.Any(a =>
                a.Equals("/demo", StringComparison.OrdinalIgnoreCase)
                || a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
            var logPath = Path.Combine(Path.GetTempPath(), "VMCreate.log");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft.Extensions.Http", Serilog.Events.LogEventLevel.Warning)
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Log.Information("VMCreate {Version} starting", ProductInfo.InformationalVersion);

            var services = new ServiceCollection();
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddSerilog(dispose: true);
            });
            services.AddHttpClient();

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
            // Fully-qualified because VMCreate.HyperV.Unattend also defines IPowerShellExecutor.
            services.AddSingleton<VMCreate.HyperV.IPowerShellExecutor, VMCreate.HyperV.PowerShellExecutor>();
            services.AddTransient<VMCreate.HyperV.Unattend.IPowerShellExecutor, VMCreate.HyperV.Unattend.PowerShellExecutor>();
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

            // ── Gallery ─────────────────────────────────────────────────────
            // Assemblies to scan for auto-discovered implementations
            var scannableAssemblies = new[]
            {
                System.Reflection.Assembly.GetExecutingAssembly(), // VMCreate (main)
                typeof(BlackArch).Assembly                         // VMCreate.Gallery.Security
            };

            // Auto-register all IGalleryLoader implementations
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

            // Also register IConfigurableCustomizationStep so pages can
            // discover distribution-specific UI options via DI.
            var configurableStepTypes = stepTypes
                .Where(t => typeof(IConfigurableCustomizationStep).IsAssignableFrom(t));
            foreach (var stepType in configurableStepTypes)
                services.AddTransient(typeof(IConfigurableCustomizationStep), stepType);

            // ── Full customization-step lookup for deploy progress mapping ─────
            services.AddTransient<IReadOnlyDictionary<string, ICustomizationStep>>(sp =>
            {
                var allSteps = sp.GetServices<ICustomizationStep>()
                    .ToLookup(s => s.Name, StringComparer.OrdinalIgnoreCase);
                // If duplicate names exist, pick the first registered instance.
                return allSteps.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            });

            // ── HTB API client (uses IHttpClientFactory) ────────────────────
            services.AddHttpClient<IHtbApiClient, HtbApiClient>();

            // ── VM creation orchestrator ────────────────────────────────────
            services.AddTransient<IVmDeploymentOrchestrator, VmDeploymentOrchestrator>();
            services.AddTransient<IVmCreator, HyperVVmCreator>();
            services.AddTransient<CreateVM>();
            services.AddSingleton<IPartitionSchemeDetector, PartitionSchemeDetector>();
            services.AddSingleton<IVmGenerationResolver, VmGenerationResolver>();

            // ── UI / pages ──────────────────────────────────────────────────
            services.AddSingleton<Func<WizardData, DeployPage>>((Func<IServiceProvider, Func<WizardData, DeployPage>>)(sp => wizardData =>
            {
                var steps = sp.GetRequiredService<IEnumerable<IConfigurableCustomizationStep>>();
                var allSteps = sp.GetRequiredService<IReadOnlyDictionary<string, ICustomizationStep>>();
                return new DeployPage(
                    wizardData,
                    sp.GetRequiredService<CreateVM>(),
                    sp.GetRequiredService<ILoggerFactory>(),
                    steps,
                    allSteps);
            }));
            services.AddSingleton<MainWindow>();

            _serviceProvider = services.BuildServiceProvider();

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            (_serviceProvider as IDisposable)?.Dispose();
            base.OnExit(e);
        }
    }
}
