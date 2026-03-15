using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CreateVM.HyperV.vmbus;
using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using VMCreate.Gallery;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    public partial class App : Application
    {
        private IServiceProvider _serviceProvider;

        private void App_OnStartup(object sender, StartupEventArgs e)
        {
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
            services.AddSingleton<IHyperVManager, PowerShellHyperVManager>();
            services.AddSingleton<ISshKeyManager, SshKeyManager>();
            services.AddTransient<IKvpSender, KvpHostToGuest>();
            services.AddTransient<IKvpPoller, HyperVKVPPoller>();
            services.AddTransient<IVmShutdownWatcher, HyperVKVPPoller>();
            services.AddTransient<IGuestDiagnosticsCollector, GuestDiagnosticsCollector>();
            services.AddTransient<IGuestShellFactory, SshGuestShellFactory>();

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

            // ── HTB API client (uses IHttpClientFactory) ────────────────────
            services.AddHttpClient<IHtbApiClient, HtbApiClient>();

            // ── VM creation orchestrator ────────────────────────────────────
            services.AddTransient<IVmCreator, HyperVVmCreator>();
            services.AddTransient<CreateVM>();
            services.AddSingleton<IPartitionSchemeDetector, PartitionSchemeDetector>();

            // ── UI / pages ──────────────────────────────────────────────────
            services.AddSingleton<Func<WizardData, DeployPage>>(sp => wizardData =>
                new DeployPage(
                    wizardData,
                    sp.GetRequiredService<CreateVM>(),
                    sp.GetRequiredService<ILoggerFactory>()));
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
