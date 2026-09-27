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
        private CancellationTokenSource? _galleryCts;
        private readonly ILogger<MainWindow> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IGalleryService _galleryService;
        private readonly Func<WizardData, DeployPage> _deployPageFactory;
        private readonly IHtbApiClient _htbApiClient;
        private readonly IEnumerable<IConfigurableCustomizationStep> _configurableSteps;
        private readonly IEnumerable<IDesktopEnvironmentCatalog> _desktopCatalogs;
        private readonly UserPreferencesStore _prefsStore;
        private readonly UserPreferences _prefs;
        private WizardData? _wizardData;

        public MainWindow(
            IGalleryService galleryService,
            Func<WizardData, DeployPage> deployPageFactory,
            IHtbApiClient htbApiClient,
            IEnumerable<IConfigurableCustomizationStep> configurableSteps,
            IEnumerable<IDesktopEnvironmentCatalog> desktopCatalogs,
            ILogger<MainWindow> logger,
            ILoggerFactory loggerFactory)
        {
            _galleryService = galleryService ?? throw new ArgumentNullException(nameof(galleryService));
            _deployPageFactory = deployPageFactory ?? throw new ArgumentNullException(nameof(deployPageFactory));
            _htbApiClient = htbApiClient ?? throw new ArgumentNullException(nameof(htbApiClient));
            _configurableSteps = configurableSteps ?? throw new ArgumentNullException(nameof(configurableSteps));
            _desktopCatalogs = desktopCatalogs ?? throw new ArgumentNullException(nameof(desktopCatalogs));
            _logger = logger;
            _loggerFactory = loggerFactory;

            InitializeComponent();

            Title = $"New Virtual Machine Wizard — v{ProductInfo.Version}";
            VersionLabel.Text = $"v{ProductInfo.Version} \u2014 {ProductInfo.CodeName}";

            // Apply persisted preferences: theme (explicit choice wins over following the
            // Windows system theme) and window size.
            _prefsStore = new UserPreferencesStore(_logger);
            _prefs = _prefsStore.Load();

            if (_prefs.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase))
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
            else if (_prefs.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase))
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);
            else
                SystemThemeWatcher.Watch(this); // follow the Windows system theme

            Width = ClampSize(_prefs.WindowWidth, MinWidth, SystemParameters.PrimaryScreenWidth, 1050);
            Height = ClampSize(_prefs.WindowHeight, MinHeight, SystemParameters.PrimaryScreenHeight, 720);

            Loaded += MyWindow_LoadedAsync;
            // First-frame milestone, then kick off the background PowerShell
            // warmup — ordering matters: the warmup thread must not compete
            // with the first render (it did when started at window-shown and
            // cost ~270 ms of first-frame).
            ContentRendered += (_, __) =>
            {
                App.RecordStartup("first-frame");
                App.StartPowerShellWarmup();
            };

            // Sync the theme toggle icon with the current theme at startup
            SyncThemeIcon();

            _mainFrame.Navigated += (_, __) => UpdateStepIndicator();
        }

        /// <summary>Clamps a persisted window dimension to a sane on-screen range.</summary>
        private static double ClampSize(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return fallback;
            return Math.Clamp(value, min, Math.Max(min, max));
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
            _prefs.Theme = next == ApplicationTheme.Dark ? "Dark" : "Light";
            SavePreferences();
            SyncThemeIcon();
        }

        private void SavePreferences()
        {
            // RestoreBounds/ActualWidth are device-dependent; guard against minimized state.
            if (WindowState == WindowState.Normal)
            {
                _prefs.WindowWidth = ActualWidth;
                _prefs.WindowHeight = ActualHeight;
            }
            _prefsStore.Save(_prefs);
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

            // Hide the Customize step only for flows that skip it (pre-built native-Hyper-V Linux
            // images, Linux ISO installers). Windows images and items with distribution options show it.
            bool skipCustomize = _wizardData?.SelectedItem != null
                && !WizardFlow.ShowsCustomizePage(_wizardData.SelectedItem, _configurableSteps);

            Step3Panel.Visibility = skipCustomize ? Visibility.Collapsed : Visibility.Visible;
            Step3Separator.Visibility = skipCustomize ? Visibility.Collapsed : Visibility.Visible;

            // A failed deployment marks the Deploy step in the indicator (stays failed while
            // navigating back to fix settings; cleared when a fresh deployment starts).
            bool deployFailed = page is DeployPage { ViewModel.HasFailed: true };

            // When Customize is skipped, renumber: Configure→2, Deploy→3.
            int activeStep = page switch
            {
                SelectImagePage => 1,
                VmSettingsPage => 2,
                VmCustomizationPage => 3,
                DeployPage => skipCustomize ? 3 : 4,
                _ => 0
            };

            SetStepStyle(Step1Icon, Step1Text, Step1ActiveBar, SymbolRegular.Image24, activeStep == 1, activeStep > 1, false);
            SetStepStyle(Step2Icon, Step2Text, Step2ActiveBar, SymbolRegular.Settings24, activeStep == 2, activeStep > 2, false);

            if (!skipCustomize)
            {
                SetStepStyle(Step3Icon, Step3Text, Step3ActiveBar, SymbolRegular.Wrench24, activeStep == 3, activeStep > 3, false);
                SetStepStyle(Step4Icon, Step4Text, Step4ActiveBar, SymbolRegular.Rocket24, activeStep == 4, false, deployFailed);
            }
            else
            {
                // Deploy is the last step when Customize is hidden
                SetStepStyle(Step4Icon, Step4Text, Step4ActiveBar, SymbolRegular.Rocket24, activeStep == 3, false, deployFailed);
            }
        }

        private static void SetStepStyle(
            SymbolIcon icon, System.Windows.Controls.TextBlock text, System.Windows.Controls.Border activeBar,
            SymbolRegular defaultSymbol, bool active, bool completed, bool failed)
        {
            string brushKey = failed
                ? "SystemFillColorCriticalBrush"
                : active ? "TextFillColorPrimaryBrush"
                : completed ? "SystemFillColorSuccessBrush"
                : "TextFillColorSecondaryBrush";

            icon.Symbol = failed ? SymbolRegular.DismissCircle24
                : completed ? SymbolRegular.CheckmarkCircle24
                : defaultSymbol;
            text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;

            activeBar.Visibility = (active || failed) ? Visibility.Visible : Visibility.Collapsed;
            activeBar.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
                failed ? "SystemFillColorCriticalBrush" : "AccentTextBrush");

            // SetResourceReference creates a live link that updates on theme change.
            icon.SetResourceReference(SymbolIcon.ForegroundProperty, brushKey);
            text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brushKey);
        }

        private async void MyWindow_LoadedAsync(object sender, RoutedEventArgs e)
        {
            _galleryCts = new CancellationTokenSource();

            // Navigate to the first page IMMEDIATELY so the UI is visible straight away.
            // Gallery items will stream into _galleryItems in the background below.
            _wizardData = new WizardData { GalleryItems = _galleryItems, DemoMode = App.DemoMode };
            var firstPage = new SelectImagePage(_wizardData, _htbApiClient, _configurableSteps, _desktopCatalogs, _loggerFactory);
            firstPage.WizardCompleted += WizardPage_Completed;
            _mainFrame.Navigate(firstPage);

            try
            {
                // ── 1. Populate from cache instantly (warm startup) ──────────
                var cachedItems = _galleryService.LoadFromCache();
                foreach (var item in cachedItems)
                {
                    if (item.Name == null || item.DiskUri == null) continue;
                    // Migration: caches written before releases became variants
                    // hold the old Kali stable/weekly twins; the live source
                    // now supplies the single "Kali Linux" item. Drop the
                    // legacy twins so both shapes never coexist in the list.
                    if (item.IsLegacyReleaseCacheEntry())
                        continue;
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
            SavePreferences();
            _galleryCts?.Cancel();
            _galleryCts?.Dispose();
            base.OnClosed(e);
        }

        private async void WizardPage_Completed(object? sender, WizardResultEventArgs e)
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
                    _wizardData = new WizardData { GalleryItems = _galleryItems, DemoMode = App.DemoMode };
                    _wizardData.ResetForNewWizard();
                    var firstPage = new SelectImagePage(_wizardData, _htbApiClient, _configurableSteps, _desktopCatalogs, _loggerFactory);
                    firstPage.WizardCompleted += WizardPage_Completed;
                    _mainFrame.Navigate(firstPage);
                    return;
                }

                // Otherwise we're on the Customize page — validate & navigate to Deploy.
                var galleryItem = _wizardData?.SelectedItem;
                var vmSettings = _wizardData?.Settings ?? new VmSettings();

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

                // ── Release boundary ──
                // The wizard carried the distro-level item plus the chosen
                // release id; deployment needs the concrete image. Swapping
                // the item here is safe: back-navigation is disabled during
                // deployment and a fresh WizardData is built for the next run.
                var effectiveItem = galleryItem.ResolveRelease(_wizardData!.SelectedReleaseId);
                if (effectiveItem == null)
                {
                    var offered = galleryItem.ReleaseVariants is { Count: > 0 } variants
                        ? string.Join(", ", variants.Select(v => v.Id))
                        : "none";
                    ShowBanner($"Unknown release '{_wizardData.SelectedReleaseId}' for {galleryItem.Name} (available: {offered}).", isError: true);
                    return;
                }
                _wizardData.SelectedItem = effectiveItem;
                galleryItem = effectiveItem;

                var deployPage = _deployPageFactory(_wizardData!);
                deployPage.WizardCompleted += WizardPage_Completed;
                deployPage.DeploymentFailed += (_, __) => UpdateStepIndicator();
                _mainFrame.Navigate(deployPage);

                // Keep the back stack so DeployPage.OnBackToCustomize (Retry / Back) can
                // return to Customize with settings preserved. Unloading DeployPage cancels
                // any in-flight deployment via Cleanup(), so a live deployment is never left
                // running when the user navigates back.
            }
        }
    }
}
