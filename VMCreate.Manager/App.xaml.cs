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

        private async void App_OnStartup(object sender, StartupEventArgs e)
        {
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
            Log.CloseAndFlush();
        }
    }
}
