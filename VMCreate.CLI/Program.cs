using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;
using VMCreate.CLI.Commands;
using VMCreate.Gallery;
using VMCreate.HyperV.Unattend;

namespace VMCreate.CLI
{
    internal static class Program
    {
        /// <summary>
        /// Assemblies the CLI scans for auto-discovered gallery loaders and
        /// customization steps — the same scan set AddCoreServices receives
        /// in App.xaml.cs. Exposed internal (InternalsVisibleTo) so
        /// <c>CliFrontEndParityTests</c> can pin it to the same set the GUI
        /// scans: a CLI that scans the wrong assembly silently registers
        /// ZERO customization steps (a Linux deploy then does no post-boot
        /// work at all, and an Auto deployment never even installs xrdp).
        /// That shipped once — the parity test makes it impossible to ship
        /// again unnoticed.
        /// </summary>
        internal static System.Reflection.Assembly[] ScannableAssemblies => new[]
        {
            typeof(VMCreate.SyncTimezoneStep).Assembly,                 // VMCreate.Core — steps + general gallery
            typeof(VMCreate.Gallery.BlackArch).Assembly                 // VMCreate.Gallery.Security
        };
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
                // SECURITY: same plaintext-log discipline as the main
                // paths — the elevated child carries unattend.xml, which
                // embeds the local administrator password. Debug stays OFF
                // by default; turn it on per-run only for injection
                // debugging.
                var injectSerilog = new Serilog.LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.File(injectLogPath, rollingInterval: RollingInterval.Day, shared: true, retainedFileCountLimit: 7)
                    .CreateLogger();

                // Named differently from the main-path container below to avoid
                // CS0136 (the main path declares its own `services` local within
                // the same enclosing method scope).
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
            // SECURITY: PLAINTEXT rolling log in %TEMP% — the same file name
            // the GUI hardening closed (App.xaml.cs). The previous Debug
            // floor captured every SSH command line, including
            // CopyContentAsync base64 chunks that embed VPN configs with
            // client certificates and private keys. The CLI was wired into
            // the solution while this floor was still open, so every
            // `vmcreate create` run reopened the GUI's leak. Debug stays
            // OFF; 7-day retention bounds how long plaintext history
            // lingers on disk (Serilog default: 31).
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.Extensions.Http", Serilog.Events.LogEventLevel.Warning)
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
                .CreateLogger();

            // ── DI container ─────────────────────────────────────────────────
            var services = new ServiceCollection();

            services.AddLogging(b =>
            {
                b.ClearProviders();
                b.AddSerilog(dispose: true);
            });
            services.AddHttpClient();

            // ── Core deployment services (shared with the GUI) ─────────────
            // Single source of truth: AddCoreServices in VMCreate.Core — the
            // CLI duplicated these registrations verbatim until the Core/GUI
            // split. Scan set: ScannableAssemblies (VMCreate.Core + Gallery
            // .Security), pinned to the GUI set by CliFrontEndParityTests.
            services.AddCoreServices(ScannableAssemblies);

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
