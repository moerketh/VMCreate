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
        public static IServiceProvider ServiceProvider { get; private set; }

        private void App_OnStartup(object sender, StartupEventArgs e)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "VMCreate.log");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
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

            // Auto-register all IGalleryLoader implementations in this assembly (except the aggregate)
            var galleryLoaderTypes = typeof(IGalleryLoader).Assembly
                .GetTypes()
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
            services.AddTransient<IGalleryLoader, AggregateGalleryLoader>();
            services.AddTransient<IGalleryItemsParser, GalleryItemsParser>();
            services.AddSingleton<IPartitionSchemeDetector, PartitionSchemeDetector>();
            services.AddSingleton<MediaHandlerFactory>();
            services.AddSingleton<DiskConverter>();
            services.AddTransient<SelectImagePage>();
            services.AddTransient<VmSettingsPage>();
            services.AddTransient<IVmCreator, HyperVVmCreator>();
            services.AddSingleton<IHyperVManager, PowerShellHyperVManager>();

            ServiceProvider = services.BuildServiceProvider();

            var mainWindow = new MainWindow();
            mainWindow.Show();
        }
    }
}
