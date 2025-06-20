using Microsoft.Extensions.DependencyInjection;
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
        private ServiceCollection services = new ServiceCollection();

        public MainWindow()
        {           
            // Add HttpClient services to enable IHttpClientFactory
            services.AddHttpClient();

            // Register all gallery loader classes
            services.AddTransient<LoadFromMicrosoftURI>();
            services.AddTransient<LoadFromRegistry>();
            services.AddTransient<LoadFromLocalJsonFile>();
            //distributions
            services.AddTransient<LoadBlackArchCurrent>();
            services.AddTransient<LoadFromUbuntuGitHub>();
            services.AddTransient<LoadKaliCurrent>();
            services.AddTransient<LoadParrotHome>();
            services.AddTransient<LoadParrotSecurity>();
            services.AddTransient<LoadParrotHtb>();
            services.AddTransient<LoadPentooCurrent>();
            //other
            services.AddTransient<LoadFromGNS3GitHub>();

            // Register AggregateGalleryLoader
            services.AddTransient<IGalleryLoader, AggregateGalleryLoader>();

            Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();

            InitializeComponent();
            GalleryListBox.ItemsSource = _galleryItems;
            SearchTextBox.TextChanged += SearchTextBox_TextChanged;
            GalleryListBox.SelectionChanged += GalleryListBox_SelectionChanged;
            CreateVMButton.Click += CreateVMButton_Click;
            Loaded += MyWindow_LoadedAsync;
        }

        private async void MyWindow_LoadedAsync(object sender, RoutedEventArgs e)
        {
            var items = await new AggregateGalleryLoader(services.BuildServiceProvider()).LoadGalleryItems();
            items.Where(i => i.Name != null && i.DiskUri != null)
                .OrderBy(i => i.Name)
                .ToList()
                .ForEach(i => _galleryItems.Add(i));

            GalleryListBox.ItemsSource = _galleryItems;

        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchTextBox.Text.ToLower();
            var filteredItems = _galleryItems.Where(item =>
                item.Name.ToLower().Contains(filter) ||
                item.Publisher.ToLower().Contains(filter) ||
                item.Description.ToLower().Contains(filter)).ToList();
            GalleryListBox.ItemsSource = filteredItems;
            Log.Debug($"Applied search filter: {filter}");
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
                Log.Debug($"Set default VM name to: {selectedItem.Name} and updated details panel");
            }
            else
            {
                DetailScreenshot.Source = null;
                DetailName.Text = "";
                DetailPublisher.Text = "";
                DetailVersion.Text = "";
                DetailLastUpdated.Text = "";
                DetailDescription.Text = "";
                Log.Debug("No item selected in gallery, cleared details panel.");
            }
        }

        private async void CreateVMButton_Click(object sender, RoutedEventArgs e)
        {

            this.CreateVMButton.IsEnabled = false; // Disable button to prevent multiple clicks
            bool completed = false;
            var vmSettings = new VmSettings();
            var galleryItem = GalleryListBox.SelectedItem as GalleryItem;
            try
            {
                if (!(GalleryListBox.SelectedItem is GalleryItem selectedItem))
                {
                    Log.Debug("No gallery item selected.");
                    throw new Exception("Please select a gallery item!");
                }
                Log.Debug($"Selected item: {selectedItem.Name}");

                if (string.IsNullOrEmpty(VMNameTextBox.Text))
                {
                    Log.Debug("VM name is empty.");
                    throw new Exception("VM Name is required!");
                }
                vmSettings.VMName = VMNameTextBox.Text.Trim();

                if (!int.TryParse(MemoryTextBox.Text, out int memoryMB) || memoryMB < 512)
                {
                    Log.Debug($"Invalid memory value: {MemoryTextBox.Text}");
                    throw new Exception("Memory must be at least 512 MB!");
                }
                vmSettings.MemoryMB = memoryMB;

                if (!int.TryParse(CPUTextBox.Text, out int cpuCount) || cpuCount < 1)
                {
                    Log.Debug($"Invalid CPU count: {CPUTextBox.Text}");
                    throw new Exception("CPU count must be at least 1!");
                }
                vmSettings.CPUCount = cpuCount;

                if (string.IsNullOrEmpty(selectedItem.DiskUri) || !selectedItem.DiskUri.StartsWith("http"))
                {
                    Log.Debug($"Invalid Disk URI: {selectedItem.DiskUri}");
                    throw new Exception($"Invalid disk URI: {selectedItem.DiskUri}");
                }

                Log.Debug($"Validated inputs: VMName={vmSettings.VMName}, Memory={memoryMB} MB, CPU={cpuCount}, DiskUri={selectedItem.DiskUri}");

                var cancellationTokenSource = new CancellationTokenSource();

                // Show progress window  
                _progressWindow = new ProgressWindow(cancellationTokenSource) { Owner = this };
                _progressWindow.Show();

                var progressReport = new Progress<CreateVMProgressInfo>((i) =>
                    _progressWindow.UpdateProgress(i)
                );

                var token = cancellationTokenSource.Token;
                var createVM = new CreateVM(new HttpFileDownloader(), new SevenZipExtractor(), new HyperVVmCreator());
                await createVM.StartCreateVMAsync(vmSettings, galleryItem, token, progressReport);
                completed = true;
            }
            catch(OperationCanceledException)
            {
                _progressWindow.Close();
                Log.Information("Operation Cancelled by user.");
            }
            catch (Exception ex)
            {
                Log.Error($"Error in Create VM setup: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _progressWindow.Close();
            }
            finally {
                this.CreateVMButton.IsEnabled = true;
            }
            if (completed)
            {
                _progressWindow.Close();
                _successWindow = new SuccessWindow { Owner = this };
                _successWindow.Show();
            }
        }
    }
}