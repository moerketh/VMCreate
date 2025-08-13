using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace VMCreate
{
    public partial class SelectImagePage : Page
    {
        public event EventHandler<WizardResultEventArgs> WizardCompleted;
        private readonly WizardData _wizardData;
        private readonly ILogger<SelectImagePage> _logger;
        private readonly ILoggerFactory _loggerFactory;

        public SelectImagePage(WizardData wizardData, ILogger<SelectImagePage> logger, ILoggerFactory loggerFactory)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            InitializeComponent();
            GalleryListBox.ItemsSource = _wizardData.GalleryItems;
            SearchTextBox.TextChanged += SearchTextBox_TextChanged;
            GalleryListBox.SelectionChanged += GalleryListBox_SelectionChanged;
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchTextBox.Text.ToLower();
            var filteredItems = _wizardData.GalleryItems.Where(item =>
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