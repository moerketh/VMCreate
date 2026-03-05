using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace VMCreate
{
    public partial class SelectImagePage : Page, INotifyPropertyChanged
    {
        public event EventHandler<WizardResultEventArgs> WizardCompleted;
        public event PropertyChangedEventHandler PropertyChanged;

        private readonly WizardData _wizardData;
        private readonly ILogger<SelectImagePage> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private bool _isLoading = true;

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
            }
        }

        /// <summary>Called by MainWindow when all gallery loaders have finished.</summary>
        public void SetLoadingComplete() => IsLoading = false;

        public SelectImagePage(WizardData wizardData, ILogger<SelectImagePage> logger, ILoggerFactory loggerFactory)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            InitializeComponent();
            DataContext = this;

            // Use a live-sorted CollectionView so items stream in alphabetically regardless
            // of the order in which individual gallery loaders complete.
            var view = CollectionViewSource.GetDefaultView(_wizardData.GalleryItems);
            view.SortDescriptions.Add(new SortDescription(nameof(GalleryItem.Name), ListSortDirection.Ascending));
            GalleryListBox.ItemsSource = view;

            // If items are already loaded (e.g. when the wizard resets after a VM creation),
            // don't show the loading overlay.
            if (_wizardData.GalleryItems.Count > 0)
                _isLoading = false;

            SearchTextBox.TextChanged += SearchTextBox_TextChanged;
            GalleryListBox.SelectionChanged += GalleryListBox_SelectionChanged;
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchTextBox.Text.Trim().ToLower();
            var view = CollectionViewSource.GetDefaultView(_wizardData.GalleryItems);
            if (string.IsNullOrEmpty(filter))
            {
                view.Filter = null;
            }
            else
            {
                view.Filter = obj => obj is GalleryItem item &&
                    (item.Name?.ToLower().Contains(filter) == true ||
                     item.Publisher?.ToLower().Contains(filter) == true ||
                     item.Description?.ToLower().Contains(filter) == true);
            }
            _logger.LogDebug("Applied search filter: {Filter}", filter);
        }

        private void GalleryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GalleryListBox.SelectedItem is GalleryItem selectedItem)
            {
                if (!string.IsNullOrEmpty(selectedItem.ThumbnailUri))
                {
                    var converter = new ImageSourceConverter();
                    DetailScreenshot.Source = converter.Convert(selectedItem.ThumbnailUri, typeof(ImageSource), null, CultureInfo.CurrentCulture) as ImageSource;
                }
                DetailName.Text = $"Name: {selectedItem.Name}";
                DetailPublisher.Text = $"Publisher: {selectedItem.Publisher}";
                DetailVersion.Text = $"Version: {selectedItem.Version}";
                DetailLastUpdated.Text = $"Last Updated: {selectedItem.LastUpdated}";
                DetailDownloadUrl.Text = $"Download URL: {selectedItem.DiskUri}";
                DetailDescription.Text = $"Description: {selectedItem.Description}";
                _logger.LogDebug("Updated details panel for: {Name}", selectedItem.Name);
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

        private void nextButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(GalleryListBox.SelectedItem is GalleryItem selectedItem))
            {
                MessageBox.Show("Please select a gallery item!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _wizardData.SelectedItem = selectedItem;
            var nextPage = new VmSettingsPage(_wizardData, _loggerFactory.CreateLogger<VmSettingsPage>(), _loggerFactory);
            nextPage.WizardCompleted += (s, args) => WizardCompleted?.Invoke(s, args);
            NavigationService.Navigate(nextPage);
        }

        private void cancelButton_Click(object sender, RoutedEventArgs e)
        {
            WizardCompleted?.Invoke(this, new WizardResultEventArgs(WizardResult.Canceled));
        }
    }

    public class WizardResultEventArgs : EventArgs
    {
        public WizardResult Result { get; }
        public WizardResultEventArgs(WizardResult result) => Result = result;
    }
}