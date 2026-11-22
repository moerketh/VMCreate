using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace VMCreate
{
    public partial class VmCustomizationPage : Page
    {
        public event EventHandler<WizardResultEventArgs>? WizardCompleted;

        public VmCustomizationPage(WizardData wizardData, IEnumerable<IConfigurableCustomizationStep> configurableSteps, IEnumerable<IDesktopEnvironmentCatalog> desktopCatalogs, ILoggerFactory loggerFactory)
        {
            if (wizardData == null) throw new ArgumentNullException(nameof(wizardData));
            if (configurableSteps == null) throw new ArgumentNullException(nameof(configurableSteps));
            if (desktopCatalogs == null) throw new ArgumentNullException(nameof(desktopCatalogs));
            if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));

            var viewModel = new VmCustomizationPageViewModel(
                wizardData, loggerFactory.CreateLogger<VmCustomizationPageViewModel>(),
                configurableSteps, desktopCatalogs);

            InitializeComponent();
            DataContext = viewModel;

            viewModel.RequestNavigateBack += () => NavigationService.GoBack();
            viewModel.RequestWizardComplete += result =>
                WizardCompleted?.Invoke(this, new WizardResultEventArgs(result));
        }
    }
}
