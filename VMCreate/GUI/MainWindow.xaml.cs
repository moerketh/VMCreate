using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
        // Tracks (Name, DiskUri) pairs already in _galleryItems to deduplicate streamed batches.
        private readonly HashSet<(string Name, string DiskUri)> _seenItems = new HashSet<(string, string)>();
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

            // Navigate to the first page IMMEDIATELY so the UI is visible straight away.
            // Gallery items will stream into _galleryItems in the background below.
            _wizardData = new WizardData { GalleryItems = _galleryItems };
            var firstPage = new SelectImagePage(
                _wizardData,
                _serviceProvider.GetRequiredService<ILogger<SelectImagePage>>(),
                _loggerFactory);
            firstPage.WizardCompleted += WizardPage_Completed;
            _mainFrame.Navigate(firstPage);

            try
            {
                var galleryLoader = _serviceProvider.GetRequiredService<IGalleryLoader>();

                if (galleryLoader is AggregateGalleryLoader aggregate)
                {
                    // Stream items into the ObservableCollection as each loader finishes.
                    // The ListBox sees every change immediately because ObservableCollection
                    // fires CollectionChanged on the UI thread via Dispatcher.BeginInvoke.
                    await aggregate.LoadGalleryItemsStreaming(batch =>
                    {
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            foreach (var item in batch)
                            {
                                if (item.Name == null || item.DiskUri == null) continue;
                                if (_seenItems.Add((item.Name, item.DiskUri)))
                                    _galleryItems.Add(item);
                            }
                        });
                    }, _galleryCts.Token);
                }
                else
                {
                    // Fallback for any non-aggregate IGalleryLoader (e.g. in tests).
                    var items = await galleryLoader.LoadGalleryItems(_galleryCts.Token);
                    foreach (var item in items
                        .Where(i => i.Name != null && i.DiskUri != null)
                        .GroupBy(i => new { i.Name, i.DiskUri })
                        .Select(g => g.First()))
                    {
                        if (_seenItems.Add((item.Name, item.DiskUri)))
                            _galleryItems.Add(item);
                    }
                }

                _logger.LogDebug("Successfully loaded gallery items");
                firstPage.SetLoadingComplete();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Gallery loading was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load gallery items");
                firstPage.SetLoadingComplete();
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