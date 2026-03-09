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
        private readonly ILogger<MainWindow> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IGalleryService _galleryService;
        private readonly Func<WizardData, DeployPage> _deployPageFactory;
        private readonly IHtbApiClient _htbApiClient;
        private WizardData _wizardData;

        public MainWindow(
            IGalleryService galleryService,
            Func<WizardData, DeployPage> deployPageFactory,
            IHtbApiClient htbApiClient,
            ILogger<MainWindow> logger,
            ILoggerFactory loggerFactory)
        {
            _galleryService = galleryService ?? throw new ArgumentNullException(nameof(galleryService));
            _deployPageFactory = deployPageFactory ?? throw new ArgumentNullException(nameof(deployPageFactory));
            _htbApiClient = htbApiClient ?? throw new ArgumentNullException(nameof(htbApiClient));
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
                DeployPage => 4,
                _ => 0
            };

            var accentBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
            var secondaryBrush = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");

            SetStepStyle(Step1Icon, Step1Text, SymbolRegular.Image24, activeStep == 1, activeStep > 1, accentBrush, secondaryBrush);
            SetStepStyle(Step2Icon, Step2Text, SymbolRegular.Settings24, activeStep == 2, activeStep > 2, accentBrush, secondaryBrush);
            SetStepStyle(Step3Icon, Step3Text, SymbolRegular.Wrench24, activeStep == 3, activeStep > 3, accentBrush, secondaryBrush);
            SetStepStyle(Step4Icon, Step4Text, SymbolRegular.Rocket24, activeStep == 4, false, accentBrush, secondaryBrush);
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
            var firstPage = new SelectImagePage(_wizardData, _htbApiClient, _loggerFactory);
            firstPage.WizardCompleted += WizardPage_Completed;
            _mainFrame.Navigate(firstPage);

            try
            {
                // ── 1. Populate from cache instantly (warm startup) ──────────
                var cachedItems = _galleryService.LoadFromCache();
                foreach (var item in cachedItems)
                {
                    if (item.Name == null || item.DiskUri == null) continue;
                    if (_seenItems.Add((item.Name, item.DiskUri)))
                        _galleryItems.Add(item);
                }
                if (cachedItems.Count > 0)
                    _logger.LogDebug("Populated {Count} items from cache.", _galleryItems.Count);

                // ── 2. Background refresh via streaming ──────────────────────
                await _galleryService.LoadFromSourcesStreamingAsync(_seenItems, batch =>
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        foreach (var item in batch)
                        {
                            if (item.Name == null || item.DiskUri == null) continue;
                            if (_seenItems.Add((item.Name, item.DiskUri)))
                            {
                                _galleryItems.Add(item);
                            }
                            else
                            {
                                // Replace stale cached item with fresh data (preserves new metadata
                                // such as Category/IsRecommended that may be missing from older caches).
                                for (int i = 0; i < _galleryItems.Count; i++)
                                {
                                    if (_galleryItems[i].Name == item.Name &&
                                        _galleryItems[i].DiskUri == item.DiskUri)
                                    {
                                        _galleryItems[i] = item;
                                        break;
                                    }
                                }
                            }
                        }
                    });
                }, _galleryCts.Token);

                // ── 3. Persist fresh results to disk cache ───────────────────
                _galleryService.SaveCache(_galleryItems.ToList());

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
                // If we're on the Deploy page, "Finished" means "New VM" — reset the wizard.
                if (sender is DeployPage)
                {
                    _wizardData = new WizardData { GalleryItems = _galleryItems };
                    var firstPage = new SelectImagePage(_wizardData, _htbApiClient, _loggerFactory);
                    firstPage.WizardCompleted += WizardPage_Completed;
                    _mainFrame.Navigate(firstPage);
                    return;
                }

                // Otherwise we're on the Customize page — validate & navigate to Deploy.
                var galleryItem = _wizardData.SelectedItem;
                var vmSettings = _wizardData.Settings;

                if (galleryItem == null)
                {
                    ShowBanner("Please select a gallery item.", isError: true);
                    return;
                }
                if (string.IsNullOrEmpty(vmSettings.VMName))
                {
                    ShowBanner("VM Name is required.", isError: true);
                    return;
                }
                if (string.IsNullOrEmpty(galleryItem.DiskUri) || !galleryItem.DiskUri.StartsWith("http"))
                {
                    ShowBanner($"Invalid disk URI: {galleryItem.DiskUri}", isError: true);
                    return;
                }

                var deployPage = _deployPageFactory(_wizardData);
                deployPage.WizardCompleted += WizardPage_Completed;
                _mainFrame.Navigate(deployPage);

                // Remove back-stack entries so the user can't navigate back during deployment.
                while (_mainFrame.CanGoBack)
                    _mainFrame.RemoveBackEntry();
            }
        }
    }
}