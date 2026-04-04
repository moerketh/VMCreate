using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VMCreate
{
    public partial class VmSettingsPage : Page
    {
        private readonly WizardData _wizardData;
        private readonly IHtbApiClient _htbApiClient;
        private readonly IEnumerable<IConfigurableCustomizationStep> _configurableSteps;
        private readonly ILoggerFactory _loggerFactory;

        public event EventHandler<WizardResultEventArgs> WizardCompleted;

        public VmSettingsPage(WizardData wizardData, IHtbApiClient htbApiClient, IEnumerable<IConfigurableCustomizationStep> configurableSteps, ILoggerFactory loggerFactory)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _htbApiClient = htbApiClient ?? throw new ArgumentNullException(nameof(htbApiClient));
            _configurableSteps = configurableSteps ?? throw new ArgumentNullException(nameof(configurableSteps));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

            var viewModel = new VmSettingsPageViewModel(
                wizardData, loggerFactory.CreateLogger<VmSettingsPageViewModel>());

            InitializeComponent();
            DataContext = viewModel;

            viewModel.RequestNavigateNext += OnNavigateNext;
            viewModel.RequestNavigateBack += () => NavigationService.GoBack();
            viewModel.RequestWizardComplete += result =>
                WizardCompleted?.Invoke(this, new WizardResultEventArgs(result));
        }

        private void OnNavigateNext()
        {
            // Native Hyper-V images (e.g. Windows) skip the customization page —
            // Linux-specific options (xRDP, SSH, VPN) don't apply.
            if (_wizardData.SelectedItem?.IsNativeHyperV == true)
            {
                WizardCompleted?.Invoke(this, new WizardResultEventArgs(WizardResult.Finished));
                return;
            }

            var nextPage = new VmCustomizationPage(_wizardData, _htbApiClient, _configurableSteps, _loggerFactory);
            nextPage.WizardCompleted += (s, args) => WizardCompleted?.Invoke(s, args);
            NavigationService.Navigate(nextPage);
        }

        // Input filtering — View-layer concern, stays in code-behind

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
    }
}