using Microsoft.Extensions.Logging;
using System;
using System.Windows.Controls;

namespace VMCreate
{
    public partial class VmCustomizationPage : Page
    {
        public event EventHandler<WizardResultEventArgs> WizardCompleted;

        public VmCustomizationPage(WizardData wizardData, IHtbApiClient htbApiClient, ILoggerFactory loggerFactory)
        {
            if (wizardData == null) throw new ArgumentNullException(nameof(wizardData));
            if (htbApiClient == null) throw new ArgumentNullException(nameof(htbApiClient));
            if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));

            var viewModel = new VmCustomizationPageViewModel(
                wizardData, htbApiClient, loggerFactory.CreateLogger<VmCustomizationPageViewModel>());

            InitializeComponent();
            DataContext = viewModel;

            viewModel.RequestNavigateBack += () => NavigationService.GoBack();
            viewModel.RequestWizardComplete += result =>
                WizardCompleted?.Invoke(this, new WizardResultEventArgs(result));
        }
    }
}