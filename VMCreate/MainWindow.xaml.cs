using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Xml.Linq;
using System.Threading;
using VMCreate;
using VMCreate.Gallery;
using Microsoft.Extensions.DependencyInjection;

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
            // Set up the service collection for dependency injection
            
            // Add HttpClient services to enable IHttpClientFactory
            services.AddHttpClient();

            // Register all gallery loader classes
            services.AddTransient<LoadBlackArchCurrent>();
            services.AddTransient<LoadFromUbuntuGitHub>();
            services.AddTransient<LoadKaliCurrent>();
            services.AddTransient<LoadParrotCurrent>();
            services.AddTransient<LoadPentooCurrent>();

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

        private void WriteLog(string message)
        {
            try
            {
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch { }
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchTextBox.Text.ToLower();
            var filteredItems = _galleryItems.Where(item =>
                item.Name.ToLower().Contains(filter) ||
                item.Publisher.ToLower().Contains(filter) ||
                item.Description.ToLower().Contains(filter)).ToList();
            GalleryListBox.ItemsSource = filteredItems;
            WriteLog($"Applied search filter: {filter}");
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
                WriteLog($"Set default VM name to: {selectedItem.Name} and updated details panel");
            }
            else
            {
                DetailScreenshot.Source = null;
                DetailName.Text = "";
                DetailPublisher.Text = "";
                DetailVersion.Text = "";
                DetailLastUpdated.Text = "";
                DetailDescription.Text = "";
                WriteLog("No item selected in gallery, cleared details panel.");
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
                    WriteLog("No gallery item selected.");
                    throw new Exception("Please select a gallery item!");
                }
                WriteLog($"Selected item: {selectedItem.Name}");

                if (string.IsNullOrEmpty(VMNameTextBox.Text))
                {
                    WriteLog("VM name is empty.");
                    throw new Exception("VM Name is required!");
                }
                vmSettings.VMName = VMNameTextBox.Text.Trim();

                if (!int.TryParse(MemoryTextBox.Text, out int memoryMB) || memoryMB < 512)
                {
                    WriteLog($"Invalid memory value: {MemoryTextBox.Text}");
                    throw new Exception("Memory must be at least 512 MB!");
                }
                vmSettings.MemoryMB = memoryMB;

                if (!int.TryParse(CPUTextBox.Text, out int cpuCount) || cpuCount < 1)
                {
                    WriteLog($"Invalid CPU count: {CPUTextBox.Text}");
                    throw new Exception("CPU count must be at least 1!");
                }
                vmSettings.CPUCount = cpuCount;

                if (string.IsNullOrEmpty(selectedItem.DiskUri) || !selectedItem.DiskUri.StartsWith("http"))
                {
                    WriteLog($"Invalid Disk URI: {selectedItem.DiskUri}");
                    throw new Exception($"Invalid disk URI: {selectedItem.DiskUri}");
                }
                
                WriteLog($"Validated inputs: VMName={vmSettings.VMName}, Memory={memoryMB} MB, CPU={cpuCount}, DiskUri={selectedItem.DiskUri}");

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

                //worker.RunWorkerCompleted += (s, args) =>
                //{
                //    progressWindow.Close();
                //    if (args.Cancelled)
                //    {
                //        WriteLog("Operation cancelled.");
                //    }
                //    else if (args.Result is Exception ex)
                //    {
                //        WriteLog($"Error in VM creation process: {ex.Message}");
                //        MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                //    }
                //    else
                //    {
                //        // Show success window  
                //        var successWindow = new SuccessWindow { Owner = this };
                //        successWindow.ShowDialog();
                //        WriteLog("Displayed success window.");
                //    }
                //    CleanupTempFiles();
                //};

                completed = true;
            }
            catch(OperationCanceledException)
            {
                _progressWindow.Close();
            }
            catch (Exception ex)
            {
                WriteLog($"Error in Create VM setup: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally {
                this.CreateVMButton.IsEnabled = true;
            }
            if (completed)
            {
                _successWindow = new SuccessWindow { Owner = this };
                _successWindow.Show();
            }

        }

    }
}