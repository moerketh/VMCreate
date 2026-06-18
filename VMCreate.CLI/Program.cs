using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CreateVM.HyperV.vmbus;
using Serilog;
using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VMCreate;
using VMCreate.CLI.Commands;
using VMCreate.Gallery;
using VMCreate.MediaHandlers;

namespace VMCreate.CLI
{
    internal static class Program
    {
        static async Task<int> Main(string[] args)
        {
            // ── Headless elevated child: --inject-unattend <vhdxPath> ────────
            // When ElevatedUnattendInjector spawns this process elevated,
            // handle the injection directly without building the command tree.
            if (args.Length >= 2 &&
                args[0].Equals("--inject-unattend", StringComparison.OrdinalIgnoreCase))
            {
                string vhdxPath = args[1];
                string injectLogPath = Path.Combine(Path.GetTempPath(), "VMCreate.inject.log");
                var injectSerilog = new Serilog.LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.File(injectLogPath, rollingInterval: RollingInterval.Day, shared: true)
                    .CreateLogger();

                var services = new ServiceCollection();
                services.AddLogging(b =>
                {
                    b.ClearProviders();
                    b.AddSerilog(injectSerilog, dispose: true);
                });
                services.AddTransient<VMCreate.HyperV.Unattend.IPowerShellExecutor, VMCreate.HyperV.Unattend.PowerShellExecutor>();
                services.AddTransient<IOfflineRegistryEditor, OfflineRegistryEditor>();
                services.AddTransient<UnattendInjector>();
                var sp = services.BuildServiceProvider();
                var injector = sp.GetRequiredService<UnattendInjector>();
                var injectLogger = sp.GetRequiredService<ILogger<UnattendInjector>>();

                try
                {
                    injectLogger.LogInformation("Elevated child: injecting unattend.xml into {VhdxPath}", vhdxPath);
                    bool ok = await injector.InjectAsync(vhdxPath, CancellationToken.None);
                    injectLogger.LogInformation("Injection result: {Result}", ok ? "succeeded" : "failed");
                    return ok ? 0 : 1;
                }
                catch (Exception ex)
                {
                    injectLogger.LogError(ex, "Injection failed: {Message}", ex.Message);
                    return 1;
                }
                finally
                {
                    (sp as IDisposable)?.Dispose();
                }
            }

            // ── Logging ──────────────────────────────────────────────────────
            var logPath = Path.Combine(Path.GetTempPath(), "VMCreate.log");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft.Extensions.Http", Serilog.Events.LogEventLevel.Warning)
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            // ── DI container ─────────────────────────────────────────────────
            var services = new ServiceCollection();

            services.AddLogging(b =>
            {
                b.ClearProviders();
                b.AddSerilog(dispose: true);
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

            // Hyper-V / VM plumbing
            services.AddSingleton<IHyperVManager, PowerShellHyperVManager>();
            services.AddSingleton<IVmLifecycleManager>(s => s.GetRequiredService<IHyperVManager>());
            services.AddSingleton<IVmDiskManager>(s => s.GetRequiredService<IHyperVManager>());
            services.AddSingleton<IVmBootManager>(s => s.GetRequiredService<IHyperVManager>());
            services.AddSingleton<IVmNetworkManager>(s => s.GetRequiredService<IHyperVManager>());
            services.AddSingleton<IVmConfigManager>(s => s.GetRequiredService<IHyperVManager>());
            services.AddSingleton<IUnattendInjector, ElevatedUnattendInjector>();
            // Fully-qualified because VMCreate.HyperV.Unattend also defines IPowerShellExecutor.
            services.AddTransient<VMCreate.HyperV.IPowerShellExecutor, VMCreate.HyperV.PowerShellExecutor>();
            services.AddTransient<VMCreate.HyperV.Unattend.IPowerShellExecutor, VMCreate.HyperV.Unattend.PowerShellExecutor>();
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
            var scannableAssemblies = new[]
            {
                System.Reflection.Assembly.GetExecutingAssembly(), // VMCreate.CLI
                typeof(VMCreate.Gallery.BlackArch).Assembly                // VMCreate.Gallery.Security
            };

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

            // ── HTB API client ────────────────────────────────────────────
            services.AddHttpClient<IHtbApiClient, HtbApiClient>();

            // ── VM creation orchestrator ────────────────────────────────────
            services.AddTransient<IVmCreator, HyperVVmCreator>();
            services.AddTransient<CreateVM>();
            services.AddSingleton<IPartitionSchemeDetector, PartitionSchemeDetector>();

            IServiceProvider provider = services.BuildServiceProvider();

            // ── Command tree ─────────────────────────────────────────────────
            var rootCommand = new RootCommand("VMCreate CLI — create and manage Hyper-V VMs from pre-built images.");

            rootCommand.AddCommand(CreateCommand.Build(provider));
            rootCommand.AddCommand(ListCommand.Build(provider));
            rootCommand.AddCommand(GalleryCommand.Build(provider));

            var parser = new CommandLineBuilder(rootCommand)
                .UseDefaults()
                .Build();

            try
            {
                return await parser.InvokeAsync(args);
            }
            finally
            {
                await Log.CloseAndFlushAsync();
                (provider as IDisposable)?.Dispose();
            }
        }
    }
}
