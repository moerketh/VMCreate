using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

            var services = new ServiceCollection();
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddSerilog(dispose: true);
            });
            services.AddHttpClient();
            services.AddTransient<IFileStreamProvider, FileStreamProvider>();
            services.AddTransient<IHttpStreamProvider, HttpStreamProvider>();
            services.AddTransient<IStreamCopierWithProgress, StreamCopierWithProgress>();
            services.AddTransient<IDownloader, HttpFileDownloader>();

            // Auto-register all IGalleryLoader implementations across main and security assemblies
            var galleryLoaderTypes = new[]
            {
                System.Reflection.Assembly.GetExecutingAssembly(), // VMCreate (main)
                typeof(BlackArch).Assembly                         // VMCreate.Gallery.Security
            }
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IGalleryLoader).IsAssignableFrom(t)
                        && !t.IsAbstract
                        && !t.IsInterface
                        && t != typeof(AggregateGalleryLoader));
            foreach (var loaderType in galleryLoaderTypes)
                services.AddTransient(loaderType);

            services.AddTransient<XzFileExtractor>();
            services.AddTransient<ArchiveExtractor>();
            services.AddTransient<IExtractor>(provider => new ExtractorFactory(
                provider.GetRequiredService<XzFileExtractor>(),
                provider.GetRequiredService<ArchiveExtractor>(),
                provider.GetRequiredService<ILogger<ExtractorFactory>>()));
            services.AddTransient<CreateVM>();
            services.AddTransient<IGalleryLoader>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<AggregateGalleryLoader>>();
                var loaders = galleryLoaderTypes.Select(t => (IGalleryLoader)provider.GetRequiredService(t));
                return new AggregateGalleryLoader(logger, loaders);
            });
            services.AddSingleton<GalleryCache>();
            services.AddTransient<IGalleryItemsParser, GalleryItemsParser>();
            services.AddSingleton<IPartitionSchemeDetector, PartitionSchemeDetector>();
            services.AddSingleton<MediaHandlerFactory>();
            services.AddSingleton<DiskConverter>();
            services.AddTransient<IVmCreator, HyperVVmCreator>();
            services.AddSingleton<IHyperVManager, PowerShellHyperVManager>();

            _serviceProvider = services.BuildServiceProvider();

            var mainWindow = new MainWindow(
                _serviceProvider,
                _serviceProvider.GetRequiredService<ILogger<MainWindow>>(),
                _serviceProvider.GetRequiredService<ILoggerFactory>());
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            (_serviceProvider as IDisposable)?.Dispose();
            base.OnExit(e);
        }
    }
}
