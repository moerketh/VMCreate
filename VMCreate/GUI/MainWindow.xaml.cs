using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VMCreate.Gallery;

namespace VMCreate
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<GalleryItem> _galleryItems = new ObservableCollection<GalleryItem>();
        private CancellationTokenSource _galleryCts;
        private ProgressWindow _progressWindow;
        private SuccessWindow _successWindow;
        private readonly ILogger<MainWindow> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServiceProvider _serviceProvider;
        private WizardData _wizardData;

        public MainWindow()
        {
            _serviceProvider = App.ServiceProvider;
            _logger = _serviceProvider.GetRequiredService<ILogger<MainWindow>>();
            _loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

            InitializeComponent();
            Loaded += MyWindow_LoadedAsync;
        }

        private async void MyWindow_LoadedAsync(object sender, RoutedEventArgs e)
        {
            _galleryCts = new CancellationTokenSource();
            try
            {
                var galleryLoader = _serviceProvider.GetRequiredService<IGalleryLoader>();
                var items = await galleryLoader.LoadGalleryItems(_galleryCts.Token);
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
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Gallery loading was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load gallery items");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _galleryCts?.Cancel();
            _galleryCts?.Dispose();
            base.OnClosed(e);
        }

        private async void WizardPage_Completed(object sender, WizardResultEventArgs e)
        {
            if (e.Result == WizardResult.Canceled)
            {
                Close();
            }
            else if (e.Result == WizardResult.Finished)
            {
                bool success = await CreateVMAsync(_wizardData);
                if (success)
                {
                    // Reset wizard data and navigate back to the first page
                    _wizardData = new WizardData { GalleryItems = _galleryItems };
                    var firstPage = new SelectImagePage(_wizardData, _serviceProvider.GetRequiredService<ILogger<SelectImagePage>>(), _loggerFactory);
                    firstPage.WizardCompleted += WizardPage_Completed;
                    _mainFrame.Navigate(firstPage);
                }
            }
        }

        private async Task<bool> CreateVMAsync(WizardData wizardData)
        {
            bool completed = false;
            var galleryItem = wizardData.SelectedItem;
            var vmSettings = wizardData.Settings;
            var vmCustomizations = wizardData.Customizations;

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
                await createVM.StartCreateVMAsync(vmSettings, vmCustomizations, galleryItem, token, progressReport);
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
                _successWindow.ShowDialog();
                _logger.LogInformation("VM creation completed successfully");
            }
            return completed;
        }        
    }
}