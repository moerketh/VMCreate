using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using VMCreate;
using VMCreate.Gallery;

namespace VMCreateVM
{
    public partial class MainWindow : Window
    {
        private readonly string logPath = Path.Combine(Path.GetTempPath(), "VMCreate.log");
        private readonly ObservableCollection<GalleryItem> _galleryItems = new ObservableCollection<GalleryItem>();
        private ProgressWindow _progressWindow;
        private SuccessWindow _successWindow;
        private readonly ILogger<MainWindow> _logger; // Use generic ILogger
        private readonly IServiceProvider _serviceProvider;

        public MainWindow()
        {
            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            // Configure services
            var services = new ServiceCollection();

            // Add Logging with Serilog
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddSerilog(dispose: true);
            });

            // Add HttpClient services
            services.AddHttpClient();

            // Register gallery loader classes
            services.AddTransient<LoadFromMicrosoftURI>();
            services.AddTransient<LoadFromRegistry>();
            services.AddTransient<LoadFromLocalJsonFile>();
            services.AddTransient<LoadBlackArchCurrent>();
            services.AddTransient<LoadFromUbuntuGitHub>();
            services.AddTransient<LoadKaliCurrent>();
            services.AddTransient<LoadParrotHome>();
            services.AddTransient<LoadParrotSecurity>();
            services.AddTransient<LoadParrotHtb>();
            services.AddTransient<LoadPentooCurrent>();
            services.AddTransient<LoadFromGNS3GitHub>();
            services.AddTransient<IGalleryLoader, AggregateGalleryLoader>();
            services.AddTransient<IDownloader, HttpFileDownloader>();
            services.AddTransient<XzFileExtractor>();
            services.AddTransient<ArchiveExtractor>();
            services.AddTransient<IExtractor>(provider => new ExtractorFactory(
                provider.GetRequiredService<XzFileExtractor>(),
                provider.GetRequiredService<ArchiveExtractor>(),
                provider.GetRequiredService<ILogger<ExtractorFactory>>()));
            services.AddTransient<CreateVM>();

            // Register DiskConverter and HyperVVmCreator
            services.AddTransient<DiskConverter>();
            services.AddTransient<IVmCreator, HyperVVmCreator>();

            // Build service provider
            _serviceProvider = services.BuildServiceProvider();

            // Resolve logger
            _logger = _serviceProvider.GetRequiredService<ILogger<MainWindow>>();

            InitializeComponent();
            GalleryListBox.ItemsSource = _galleryItems;
            SearchTextBox.TextChanged += SearchTextBox_TextChanged;
            GalleryListBox.SelectionChanged += GalleryListBox_SelectionChanged;
            CreateVMButton.Click += CreateVMButton_Click;
            Loaded += MyWindow_LoadedAsync;
        }

        private async void MyWindow_LoadedAsync(object sender, RoutedEventArgs e)
        {
            try
            {
                var galleryLoader = _serviceProvider.GetRequiredService<IGalleryLoader>();
                var items = await galleryLoader.LoadGalleryItems();
                items.Where(i => i.Name != null && i.DiskUri != null)
                    .OrderBy(i => i.Name)
                    .ToList()
                    .ForEach(i => _galleryItems.Add(i));

                GalleryListBox.ItemsSource = _galleryItems;
                _logger.LogDebug("Successfully loaded gallery items");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load gallery items");
            }
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchTextBox.Text.ToLower();
            var filteredItems = _galleryItems.Where(item =>
                item.Name.ToLower().Contains(filter) ||
                item.Publisher.ToLower().Contains(filter) ||
                item.Description.ToLower().Contains(filter)).ToList();
            GalleryListBox.ItemsSource = filteredItems;
            _logger.LogDebug("Applied search filter: {Filter}", filter);
        }

        private void GalleryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GalleryListBox.SelectedItem is GalleryItem selectedItem)
            {
                VMNameTextBox.Text = selectedItem.Name;
                if (!string.IsNullOrEmpty(selectedItem.ThumbnailUri))
                {
                    DetailScreenshot.Source = new BitmapImage(new Uri(selectedItem.ThumbnailUri));
                }
                DetailName.Text = $"Name: {selectedItem.Name}";
                DetailPublisher.Text = $"Publisher: {selectedItem.Publisher}";
                DetailVersion.Text = $"Version: {selectedItem.Version}";
                DetailLastUpdated.Text = $"Last Updated: {selectedItem.LastUpdated}";
                DetailDescription.Text = $"Description: {selectedItem.Description}";
                _logger.LogDebug("Set default VM name to: {Name} and updated details panel", selectedItem.Name);
            }
            else
            {
                DetailScreenshot.Source = null;
                DetailName.Text = "";
                DetailPublisher.Text = "";
                DetailVersion.Text = "";
                DetailLastUpdated.Text = "";
                DetailDescription.Text = "";
                _logger.LogDebug("No item selected in gallery, cleared details panel");
            }
        }

        private async void CreateVMButton_Click(object sender, RoutedEventArgs e)
        {
            CreateVMButton.IsEnabled = false;
            bool completed = false;
            var vmSettings = new VmSettings();
            var galleryItem = GalleryListBox.SelectedItem as GalleryItem;

            try
            {
                if (!(GalleryListBox.SelectedItem is GalleryItem selectedItem))
                {
                    _logger.LogDebug("No gallery item selected");
                    throw new Exception("Please select a gallery item!");
                }
                _logger.LogDebug("Selected item: {Name}", selectedItem.Name);

                if (string.IsNullOrEmpty(VMNameTextBox.Text))
                {
                    _logger.LogDebug("VM name is empty");
                    throw new Exception("VM Name is required!");
                }
                vmSettings.VMName = $"{VMNameTextBox.Text.Trim()}_{DateTime.Now:yyyyMMddHHmmss}";

                if (!int.TryParse(MemoryTextBox.Text, out int memoryMB) || memoryMB < 512)
                {
                    _logger.LogDebug("Invalid memory value: {Value}", MemoryTextBox.Text);
                    throw new Exception("Memory must be at least 512 MB!");
                }
                vmSettings.MemoryMB = memoryMB;

                if (!int.TryParse(CPUTextBox.Text, out int cpuCount) || cpuCount < 1)
                {
                    _logger.LogDebug("Invalid CPU count: {Value}", CPUTextBox.Text);
                    throw new Exception("CPU count must be at least 1!");
                }
                vmSettings.CPUCount = cpuCount;

                if (string.IsNullOrEmpty(selectedItem.DiskUri) || !selectedItem.DiskUri.StartsWith("http"))
                {
                    _logger.LogDebug("Invalid Disk URI: {Uri}", selectedItem.DiskUri);
                    throw new Exception($"Invalid disk URI: {selectedItem.DiskUri}");
                }
                
                _logger.LogDebug("Validated inputs: VMName={VMName}, Memory={Memory}MB, CPU={CPU}, DiskUri={DiskUri}",
                    vmSettings.VMName, memoryMB, cpuCount, selectedItem.DiskUri);
                

                var cancellationTokenSource = new CancellationTokenSource();
                _progressWindow = new ProgressWindow(cancellationTokenSource) { Owner = this };
                _progressWindow.Show();

                var progressReport = new Progress<CreateVMProgressInfo>(i =>
                    _progressWindow.UpdateProgress(i)
                );

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
                _progressWindow.Close();
            }
            finally
            {
                CreateVMButton.IsEnabled = true;
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