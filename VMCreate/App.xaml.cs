using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VMCreate;
using VMCreate.Gallery;
using VMCreate.HyperV.Unattend;

namespace VMCreate
{
    public partial class App : Application
    {
        // Initialized during OnStartup before any consumer access; null only
        // before startup completes (the process exits if startup fails).
        private IServiceProvider? _serviceProvider;

        /// <summary>When true, VMConnect is launched automatically after the VM starts.</summary>
        internal static bool DemoMode { get; private set; }

        // ── Startup timing (permanent, always-on) ─────────────────────────
        // Started before any DI construction; milestones are logged at
        // Information so a normal run's log answers "what took so long"
        // without a profiler. ElapsedMilliseconds is cheap; the stopwatch
        // runs for the life of the process. See RecordStartup for details.
        private static readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();

        /// <summary>
        /// Logs a startup milestone at Information: elapsed ms since process
        /// start (first static access) plus the marker name. Called from the
        /// startup path (App + MainWindow first frame).
        /// </summary>
        internal static void RecordStartup(string milestone)
        {
            Log.Information("Startup: {ElapsedMs} ms — {Milestone}",
                _startupStopwatch.ElapsedMilliseconds, milestone);
        }

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
                // SECURITY: same plaintext-log discipline as the main path
                // below — the elevated child carries unattend.xml, which
                // embeds the local administrator password. Debug stays OFF
                // by default; turn it on per-run only for injection
                // debugging.
                var injectSerilog = new Serilog.LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.File(injectLogPath, rollingInterval: RollingInterval.Day, shared: true, retainedFileCountLimit: 7)
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
            // SECURITY: this is a PLAINTEXT rolling log in %TEMP%. The
            // previous MinimumLevel.Debug() floor captured every SSH command
            // line — including CopyContentAsync base64 chunks that embed VPN
            // configs with client certificates and private keys. Debug stays
            // OFF for the file sink; the transports log the information needed
            // for diagnosis at Information/Warning.
            // 7-day retention: these files are plaintext; Serilog's
            // default 31-day window keeps a month of deployment history on
            // disk for no diagnostic value.
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.Extensions.Http", Serilog.Events.LogEventLevel.Warning)
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
                .CreateLogger();

            Log.Information("VMCreate {Version} starting", ProductInfo.InformationalVersion);
            RecordStartup("process-entry"); // the stopwatch itself starts in the static ctor, just before this

            var services = new ServiceCollection();
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddSerilog(dispose: true);
            });
            services.AddHttpClient();

            // ── Core deployment services (shared with the CLI) ──────────────
            // Single source of truth: AddCoreServices in VMCreate.Core — the
            // GUI duplicated these registrations verbatim until the Core/GUI
            // split moved every non-UI service out of this assembly. Scan set =
            // VMCreate.Core (steps + general gallery) + Gallery.Security,
            // matching the CLI's ScannableAssemblies (CliFrontEndParityTests
            // pins the two sets equivalent).
            services.AddCoreServices(
                typeof(SyncTimezoneStep).Assembly,   // VMCreate.Core — steps + general gallery
                typeof(BlackArch).Assembly);         // VMCreate.Gallery.Security

            // ── Full customization-step lookup for deploy progress mapping ─────
            // GUI-only registration (the CLI container never had it): DeployPage
            // maps running steps by name through this lookup.
            services.AddTransient<IReadOnlyDictionary<string, ICustomizationStep>>(sp =>
            {
                var allSteps = sp.GetServices<ICustomizationStep>()
                    .ToLookup(s => s.Name, StringComparer.OrdinalIgnoreCase);
                // If duplicate names exist, pick the first registered instance.
                return allSteps.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            });

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
            RecordStartup("di-built");

            var mainWindow = _serviceProvider!.GetRequiredService<MainWindow>();
            mainWindow.Show();
            RecordStartup("window-shown");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            (_serviceProvider as IDisposable)?.Dispose();
            base.OnExit(e);
        }
    }
}
