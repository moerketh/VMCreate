using Microsoft.Extensions.Logging;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace VMCreate
{
    public partial class VmSettingsPage : Page
    {
        public event EventHandler<WizardResultEventArgs> WizardCompleted;
        private readonly WizardData _wizardData;
        private readonly ILogger<VmSettingsPage> _logger;

        public VmSettingsPage(WizardData wizardData, ILogger<VmSettingsPage> logger)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            InitializeComponent();
            DataContext = _wizardData;
            VMNameTextBox.Text = _wizardData.SelectedItem?.Name ?? "";
            NewDriveSizeTextBox.Text = _wizardData.Settings.NewDriveSizeInGB.ToString();
            UpdateVisibility(); // Set visibility for banner and new drive field
        }

        private void UpdateVisibility()
        {
            bool isNotVhdX = string.Equals(_wizardData.SelectedItem?.FileType, "VHDX", StringComparison.OrdinalIgnoreCase) == false;
            ConversionBanner.Visibility = isNotVhdX ? Visibility.Visible : Visibility.Collapsed;
            NewDriveLabel.Visibility = isNotVhdX ? Visibility.Visible : Visibility.Collapsed;
            NewDriveSizeTextBox.Visibility = isNotVhdX ? Visibility.Visible : Visibility.Collapsed;
        }

        private void finishButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(VMNameTextBox.Text))
                {
                    throw new Exception("VM Name is required!");
                }
                _wizardData.Settings.VMName = $"{VMNameTextBox.Text.Trim()}_{DateTime.Now:yyyyMMddHHmmss}";

                if (!int.TryParse(MemoryTextBox.Text, out int memoryMB) || memoryMB < 512)
                {
                    throw new Exception("Memory must be at least 512 MB!");
                }
                _wizardData.Settings.MemoryInMB = memoryMB;

                if (!int.TryParse(CPUTextBox.Text, out int cpuCount) || cpuCount < 1)
                {
                    throw new Exception("CPU count must be at least 1!");
                }
                _wizardData.Settings.CPUCount = cpuCount;

                _wizardData.Settings.VirtualizationEnabled = VirtualizationEnabledCheckBox.IsChecked ?? false;

                if (NewDriveSizeTextBox.Visibility == Visibility.Visible)
                {
                    if (!int.TryParse(NewDriveSizeTextBox.Text, out int sizeGB) || sizeGB < 10)
                    {
                        throw new Exception("New Drive Size must be at least 10 GB!");
                    }
                    _wizardData.Settings.NewDriveSizeInGB = sizeGB;
                }

                _logger.LogDebug("Validated VM settings: VMName={VMName}, Memory={Memory}MB, CPU={CPU}, VirtualizationEnabled={VirtualizationEnabled}, NewDriveSizeGB={NewDriveSizeGB}",
                    _wizardData.Settings.VMName, _wizardData.Settings.MemoryInMB, _wizardData.Settings.CPUCount, _wizardData.Settings.VirtualizationEnabled, _wizardData.Settings.NewDriveSizeInGB);

                WizardCompleted?.Invoke(this, new WizardResultEventArgs(WizardResult.Finished));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in VM settings validation");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void TextBoxPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                Regex regex = new Regex("[^0-9]+");
                if (regex.IsMatch(text))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
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