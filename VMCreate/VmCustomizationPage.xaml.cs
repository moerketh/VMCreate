using Microsoft.Extensions.Logging;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace VMCreate
{
    public partial class VmCustomizationPage : Page
    {
        private readonly WizardData _wizardData;
        private readonly ILogger<VmCustomizationPage> _logger;
        public event EventHandler<WizardResultEventArgs> WizardCompleted;

        public VmCustomizationPage(WizardData wizardData, ILogger<VmCustomizationPage> logger)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            InitializeComponent();
            DataContext = _wizardData;
            XrdpCheckbox.IsChecked = true;
            UpdateVisibility(); // Set visibility for banner and new drive field
        }

        private void UpdateVisibility()
        {
            bool isNotVhdX = string.Equals(_wizardData.SelectedItem?.FileType, "VHDX", StringComparison.OrdinalIgnoreCase) == false;
            ConversionBanner.Visibility = isNotVhdX ? Visibility.Visible : Visibility.Collapsed;
        }

        private void finishButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _wizardData.Customizations.ConfigureXrdp = XrdpCheckbox.IsChecked ?? false;
                WizardCompleted?.Invoke(this, new WizardResultEventArgs(WizardResult.Finished));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in VM customization");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void backButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.GoBack();
        }

        private void cancelButton_Click(object sender, RoutedEventArgs e)
        {
            WizardCompleted?.Invoke(this, new WizardResultEventArgs(WizardResult.Canceled));
        }
    }
}