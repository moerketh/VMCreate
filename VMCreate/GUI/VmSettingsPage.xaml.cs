using Microsoft.Extensions.Logging;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VMCreate
{
    public partial class VmSettingsPage : Page
    {
        private readonly WizardData _wizardData;
        private readonly ILoggerFactory _loggerFactory;

        public event EventHandler<WizardResultEventArgs> WizardCompleted;

        public VmSettingsPage(WizardData wizardData, ILoggerFactory loggerFactory)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
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
            var nextPage = new VmCustomizationPage(_wizardData, _loggerFactory);
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