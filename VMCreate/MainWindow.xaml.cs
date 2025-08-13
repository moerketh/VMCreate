using CreateVM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using VMCreate.Gallery;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    public partial class MainWindow : Window
    {
        private readonly string logPath = Path.Combine(Path.GetTempPath(), "VMCreate.log");
        private readonly ObservableCollection<GalleryItem> _galleryItems = new ObservableCollection<GalleryItem>();
        private ProgressWindow _progressWindow;
        private SuccessWindow _successWindow;
        private readonly ILogger<MainWindow> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServiceProvider _serviceProvider;
        private WizardData _wizardData;

        public MainWindow()
        {
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
            services.AddTransient<LoadFromMicrosoftURI>();
            services.AddTransient<LoadFromRegistry>();
            services.AddTransient<LoadFromLocalJsonFile>();
            services.AddTransient<FedoraSilverblue>();
            services.AddTransient<Arch>();
            services.AddTransient<PwnCloudOS>();
            services.AddTransient<BlackArch>();
            services.AddTransient<NixOS>();
            services.AddTransient<Ubuntu>();
            services.AddTransient<ClearLinux>();
            services.AddTransient<LoadKaliCurrent>();
            services.AddTransient<LoadParrotHome>();
            services.AddTransient<LoadParrotSecurity>();
            services.AddTransient<LoadParrotHtb>();
            services.AddTransient<LoadPentooCurrent>();
            services.AddTransient<LoadFedoraSecurityLab>();
            services.AddTransient<LoadFromGNS3GitHub>();
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

            _serviceProvider = services.BuildServiceProvider();
            _logger = _serviceProvider.GetRequiredService<ILogger<MainWindow>>();
            _loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

            InitializeComponent();
            Loaded += MyWindow_LoadedAsync;
        }

        private async void MyWindow_LoadedAsync(object sender, RoutedEventArgs e)
        {
            try
            {
                var galleryLoader = _serviceProvider.GetRequiredService<IGalleryLoader>();
                var items = await galleryLoader.LoadGalleryItems();
                // Filter, group by the unique key (Name and DiskUri) to remove duplicates, 
                // select the first item from each group, then order by Name, 
                // and finally add to the collection.
                items.Where(i => i.Name != null && i.DiskUri != null)
                    .GroupBy(i => new { i.Name, i.DiskUri })
                    .Select(g => g.First())
                    .OrderBy(i => i.Name)
                    .ToList()
                    .ForEach(i => _galleryItems.Add(i));

                _logger.LogDebug("Successfully loaded gallery items");

                _wizardData = new WizardData { GalleryItems = _galleryItems };
                var firstPage = new SelectImagePage(_wizardData, _serviceProvider.GetRequiredService<ILogger<SelectImagePage>>(), _loggerFactory);
                firstPage.WizardCompleted += WizardPage_Completed;
                _mainFrame.Navigate(firstPage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load gallery items");
            }
        }

        private void WizardPage_Completed(object sender, WizardResultEventArgs e)
        {
            if (e.Result == WizardResult.Canceled)
            {
                Close();
            }
            else if (e.Result == WizardResult.Finished)
            {
                CreateVM(_wizardData);
            }
        }

        private async void CreateVM(WizardData wizardData)
        {
            bool completed = false;
            var galleryItem = wizardData.SelectedItem;
            var vmSettings = wizardData.Settings;

            try
            {
                if (galleryItem == null)
                {
                    _logger.LogDebug("No gallery item selected");
                    throw new Exception("Please select a gallery item!");
                }
                if (string.IsNullOrEmpty(vmSettings.VMName))
                {
                    _logger.LogDebug("VM name is empty");
                    throw new Exception("VM Name is required!");
                }
                if (string.IsNullOrEmpty(galleryItem.DiskUri) || !galleryItem.DiskUri.StartsWith("http"))
                {
                    _logger.LogDebug("Invalid Disk URI: {Uri}", galleryItem.DiskUri);
                    throw new Exception($"Invalid disk URI: {galleryItem.DiskUri}");
                }

                _logger.LogDebug("Validated inputs: VMName={VMName}, Memory={Memory}MB, CPU={CPU}, DiskUri={DiskUri}, VirtualizationEnabled={VirtualizationEnabled}",
                    vmSettings.VMName, vmSettings.MemoryInMB, vmSettings.CPUCount, galleryItem.DiskUri, vmSettings.VirtualizationEnabled);

                var cancellationTokenSource = new CancellationTokenSource();
                _progressWindow = new ProgressWindow(cancellationTokenSource) { Owner = this };
                _progressWindow.Show();

                var progressReport = new Progress<CreateVMProgressInfo>(i =>
                    _progressWindow.UpdateProgress(i));

                var token = cancellationTokenSource.Token;
                var createVM = _serviceProvider.GetRequiredService<CreateVM>();
                await createVM.StartCreateVMAsync(vmSettings, galleryItem, token, progressReport);
                completed = true;
            }
            catch (OperationCanceledException)
            {
                _progressWindow.Close();
                _logger.LogInformation("Operation cancelled by user");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Create VM setup");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                if (_progressWindow != null) _progressWindow.Close();
            }

            if (completed)
            {
                _progressWindow.Close();
                _successWindow = new SuccessWindow { Owner = this };
                _successWindow.Show();
                _logger.LogInformation("VM creation completed successfully");
            }
        }
    }
}