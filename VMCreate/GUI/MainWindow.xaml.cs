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
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace VMCreate
{
    public partial class MainWindow : FluentWindow
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

        public MainWindow(IServiceProvider serviceProvider, ILogger<MainWindow> logger, ILoggerFactory loggerFactory)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _loggerFactory = loggerFactory;

            InitializeComponent();

            // Follow the Windows system theme by default
            SystemThemeWatcher.Watch(this);

            Loaded += MyWindow_LoadedAsync;

            // Sync the theme toggle icon with the current theme at startup
            SyncThemeIcon();

            _mainFrame.Navigated += (_, __) => UpdateStepIndicator();
        }

        private void ShowBanner(string message, bool isError)
        {
            NotificationBar.Title = isError ? "Error" : "Success";
            NotificationBar.Message = message;
            NotificationBar.Severity = isError
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Success;
            NotificationBar.IsOpen = true;
        }

        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            var current = ApplicationThemeManager.GetAppTheme();
            var next = current == ApplicationTheme.Dark
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark;
            ApplicationThemeManager.Apply(next, WindowBackdropType.Mica, true);
            SyncThemeIcon();
        }

        private void SyncThemeIcon()
        {
            var theme = ApplicationThemeManager.GetAppTheme();
            ThemeToggleButton.Icon = theme == ApplicationTheme.Dark
                ? new SymbolIcon(SymbolRegular.WeatherMoon24)
                : new SymbolIcon(SymbolRegular.WeatherSunny24);
        }

        private void UpdateStepIndicator()
        {
            var page = _mainFrame.Content;
            int activeStep = page switch
            {
                SelectImagePage => 1,
                VmSettingsPage => 2,
                VmCustomizationPage => 3,
                _ => 0
            };

            var accentBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
            var secondaryBrush = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");

            SetStepStyle(Step1Icon, Step1Text, SymbolRegular.Image24, activeStep == 1, activeStep > 1, accentBrush, secondaryBrush);
            SetStepStyle(Step2Icon, Step2Text, SymbolRegular.Settings24, activeStep == 2, activeStep > 2, accentBrush, secondaryBrush);
            SetStepStyle(Step3Icon, Step3Text, SymbolRegular.Wrench24, activeStep == 3, false, accentBrush, secondaryBrush);
        }

        private static void SetStepStyle(
            SymbolIcon icon, System.Windows.Controls.TextBlock text,
            SymbolRegular defaultSymbol, bool active, bool completed,
            System.Windows.Media.Brush accentBrush, System.Windows.Media.Brush secondaryBrush)
        {
            if (completed)
            {
                icon.Symbol = SymbolRegular.CheckmarkCircle24;
                icon.Foreground = accentBrush;
                text.Foreground = accentBrush;
                text.FontWeight = FontWeights.Normal;
            }
            else if (active)
            {
                icon.Symbol = defaultSymbol;
                icon.Foreground = accentBrush;
                text.Foreground = accentBrush;
                text.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                icon.Symbol = defaultSymbol;
                text.Foreground = secondaryBrush;
                icon.Foreground = secondaryBrush;
                text.FontWeight = FontWeights.Normal;
            }
        }

        private async void MyWindow_LoadedAsync(object sender, RoutedEventArgs e)
        {
            _galleryCts = new CancellationTokenSource();

            // Navigate to the first page IMMEDIATELY so the UI is visible straight away.
            // Gallery items will stream into _galleryItems in the background below.
            _wizardData = new WizardData { GalleryItems = _galleryItems };
            var firstPage = new SelectImagePage(_wizardData, _loggerFactory);
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
                firstPage.ShowError(
                    $"Failed to load the VM gallery: {ex.Message}. Check your internet connection and restart the application.");
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
                    var firstPage = new SelectImagePage(_wizardData, _loggerFactory);
                    firstPage.WizardCompleted += WizardPage_Completed;
                    _mainFrame.Navigate(firstPage);
                }
            }
        }

        private async Task<bool> CreateVMAsync(WizardData wizardData)
        {
            var galleryItem = wizardData.SelectedItem;
            var vmSettings = wizardData.Settings;
            var vmCustomizations = wizardData.Customizations;

            // Guard clauses — validate before showing the progress window
            if (galleryItem == null)
            {
                _logger.LogDebug("No gallery item selected");
                ShowBanner("Please select a gallery item.", isError: true);
                return false;
            }
            if (string.IsNullOrEmpty(vmSettings.VMName))
            {
                _logger.LogDebug("VM name is empty");
                ShowBanner("VM Name is required.", isError: true);
                return false;
            }
            if (string.IsNullOrEmpty(galleryItem.DiskUri) || !galleryItem.DiskUri.StartsWith("http"))
            {
                _logger.LogDebug("Invalid Disk URI: {Uri}", galleryItem.DiskUri);
                ShowBanner($"Invalid disk URI: {galleryItem.DiskUri}", isError: true);
                return false;
            }

            _logger.LogDebug("Validated inputs: VMName={VMName}, Memory={Memory}MB, CPU={CPU}, DiskUri={DiskUri}, VirtualizationEnabled={VirtualizationEnabled}",
                vmSettings.VMName, vmSettings.MemoryInMB, vmSettings.CPUCount, galleryItem.DiskUri, vmSettings.VirtualizationEnabled);

            // Append timestamp once, just before creation, so Back/Next doesn't re-append.
            vmSettings.VMName = $"{vmSettings.VMName}_{DateTime.Now:yyyyMMddHHmmss}";

            var cancellationTokenSource = new CancellationTokenSource();
            _progressWindow = new ProgressWindow(cancellationTokenSource) { Owner = this };
            _progressWindow.Show();
            IsEnabled = false;

            bool completed = false;
            try
            {
                var progressReport = new Progress<CreateVMProgressInfo>(i =>
                    _progressWindow.UpdateProgress(i));

                var token = cancellationTokenSource.Token;
                var createVM = _serviceProvider.GetRequiredService<CreateVM>();
                await createVM.StartCreateVMAsync(vmSettings, vmCustomizations, galleryItem, token, progressReport);
                completed = true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operation cancelled by user");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Create VM setup");
                ShowBanner($"VM creation failed: {ex.Message}", isError: true);
            }
            finally
            {
                IsEnabled = true;
                _progressWindow.Close();
            }

            if (completed)
            {
                _successWindow = new SuccessWindow(vmSettings.VMName) { Owner = this };
                _successWindow.ShowDialog();
                _logger.LogInformation("VM creation completed successfully");
            }
            return completed;
        }        
    }
}