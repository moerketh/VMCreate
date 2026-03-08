using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
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
            var firstPage = new SelectImagePage(_wizardData, _loggerFactory);
            firstPage.WizardCompleted += WizardPage_Completed;
            _mainFrame.Navigate(firstPage);

            try
            {
                var cache = _serviceProvider.GetRequiredService<GalleryCache>();

                // ── 1. Populate from cache instantly (warm startup) ──────────
                if (cache.TryLoadCache(out var cachedItems))
                {
                    foreach (var item in cachedItems)
                    {
                        if (item.Name == null || item.DiskUri == null) continue;
                        if (_seenItems.Add((item.Name, item.DiskUri)))
                            _galleryItems.Add(item);
                    }
                    _logger.LogDebug("Populated {Count} items from cache.", _galleryItems.Count);
                }

                // ── 2. Background refresh via streaming ──────────────────────
                var galleryLoader = _serviceProvider.GetRequiredService<IGalleryLoader>();

                if (galleryLoader is AggregateGalleryLoader aggregate)
                {
                    await aggregate.LoadGalleryItemsStreaming(batch =>
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

                // ── 3. Persist fresh results to disk cache ───────────────────
                cache.SaveCache(_galleryItems.ToList());

                _logger.LogDebug("Successfully loaded gallery items");
                firstPage.SetLoadingComplete();

                // ── 4. Resolve X profile photos in the background ────────────
                _ = ResolveXProfilePhotosAsync();
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

        /// <summary>
        /// For every <see cref="GalleryItem"/> that has <see cref="GalleryItem.XHandle"/> set,
        /// resolves the current profile photo from X and updates <see cref="GalleryItem.SymbolUri"/>.
        /// Runs as a fire-and-forget task after the gallery finishes loading so the list is
        /// usable immediately (items already carry a fallback icon).
        /// </summary>
        private async Task ResolveXProfilePhotosAsync()
        {
            try
            {
                var factory = _serviceProvider.GetService<IHttpClientFactory>();
                if (factory == null) return;
                var client = factory.CreateClient();

                // Snapshot unique X handles to resolve
                var handles = _galleryItems
                    .Where(i => !string.IsNullOrEmpty(i.XHandle))
                    .Select(i => i.XHandle)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var handle in handles)
                {
                    if (_galleryCts?.IsCancellationRequested == true) break;

                    _logger.LogDebug("Resolving X profile photo for @{Handle}", handle);

                    var resolved = await XProfilePhoto.ResolveAsync(
                        client, handle,
                        _galleryCts?.Token ?? CancellationToken.None);

                    _logger.LogDebug("Resolved @{Handle} → {Resolved}", handle, resolved ?? "(null)");

                    if (string.IsNullOrEmpty(resolved))
                        continue;

                    // Update SymbolUri on matching items. We remove + re-insert rather than
                    // self-replacing (items[i] = items[i]) because ListCollectionView with
                    // CustomSort can swallow Replace events for the same object reference.
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        for (int i = _galleryItems.Count - 1; i >= 0; i--)
                        {
                            if (string.Equals(_galleryItems[i].XHandle, handle,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                var item = _galleryItems[i];
                                item.SymbolUri = resolved;
                                _galleryItems.RemoveAt(i);
                                _galleryItems.Insert(i, item);
                            }
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve one or more X profile photos.");
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
                    var firstPage = new SelectImagePage(_wizardData, _loggerFactory);
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

                var deployPage = new DeployPage(_wizardData, _serviceProvider, _loggerFactory);
                deployPage.WizardCompleted += WizardPage_Completed;
                _mainFrame.Navigate(deployPage);

                // Remove back-stack entries so the user can't navigate back during deployment.
                while (_mainFrame.CanGoBack)
                    _mainFrame.RemoveBackEntry();
            }
        }
    }
}