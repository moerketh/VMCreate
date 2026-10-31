using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using VMCreate.Manager.HyperV;
using VMCreate.Manager.Inventory;
using VMCreate.Manager.Metadata;
using VMCreate.Manager.ViewModels;
using VMCreate.Metadata;

namespace VMCreate.Manager
{
    public partial class App : Application
    {
        private ServiceProvider? _services;

        /// <summary>Rolling plaintext log; Serilog adds the date to the file name.</summary>
        internal static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "VMCreate.Manager.log");

        private const string InstanceMutexName = @"Local\VMCreate.Manager.SingleInstance";
        private const string ActivateEventName = @"Local\VMCreate.Manager.Activate";
        private Mutex? _instanceMutex;
        private EventWaitHandle? _activateEvent;
        private RegisteredWaitHandle? _activateWait;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);
        private const int ASFW_ANY = -1;

        /// <summary>
        /// One Manager per session: two instances would fight over refreshes
        /// and the log file. A second start signals the first to come to the
        /// front and exits.
        /// </summary>
        private bool TryBecomeSingleInstance()
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(ActivateEventName);
                    // The running instance is in the background; let it take the foreground.
                    AllowSetForegroundWindow(ASFW_ANY);
                    existing.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // First instance is still starting up; nothing to activate yet.
                }
                return false;
            }

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
                (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront()),
                null, Timeout.Infinite, executeOnlyOnce: false);
            return true;
        }

        private async void App_OnStartup(object sender, StartupEventArgs e)
        {
            if (!TryBecomeSingleInstance())
            {
                Shutdown();
                return;
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(LogFilePath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
                .CreateLogger();
            Log.Information("VMCreate.Manager starting");

            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error(args.Exception, "Unhandled UI exception");
                System.Windows.MessageBox.Show(args.Exception.Message, "VMCreate Manager", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            var services = new ServiceCollection();
            services.AddLogging(b =>
            {
                b.ClearProviders();
                b.AddSerilog(dispose: true);
            });
            services.AddSingleton<IHyperVHost, WmiHyperVHost>();
            services.AddSingleton<IVmInventoryService, VmInventoryService>();
            services.AddSingleton<JsonVmMetadataStore>();
            services.AddSingleton<IVmMetadataStore>(sp => sp.GetRequiredService<JsonVmMetadataStore>());
            services.AddSingleton<ICredentialProtector, DpapiCredentialProtector>();
            services.AddSingleton<VmMetadataSync>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
            _services = services.BuildServiceProvider();

            var window = _services.GetRequiredService<MainWindow>();
            window.Show();
            await _services.GetRequiredService<MainViewModel>().InitializeAsync();
        }

        private void App_OnExit(object sender, ExitEventArgs e)
        {
            _services?.Dispose();
            _activateWait?.Unregister(null);
            _activateEvent?.Dispose();
            _instanceMutex?.Dispose();
            Log.CloseAndFlush();
        }
    }
}
