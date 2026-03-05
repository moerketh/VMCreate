using Microsoft.Extensions.Logging;
using System;
using System.Windows.Controls;

namespace VMCreate
{
    public partial class SelectImagePage : Page
    {
        public event EventHandler<WizardResultEventArgs> WizardCompleted;

        private readonly SelectImagePageViewModel _viewModel;
        private readonly WizardData _wizardData;
        private readonly ILoggerFactory _loggerFactory;

        public SelectImagePage(WizardData wizardData, ILoggerFactory loggerFactory)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

            _viewModel = new SelectImagePageViewModel(
                wizardData, loggerFactory.CreateLogger<SelectImagePageViewModel>());

            InitializeComponent();
            DataContext = _viewModel;

            _viewModel.RequestNavigateNext += OnNavigateNext;
            _viewModel.RequestWizardComplete += result =>
                WizardCompleted?.Invoke(this, new WizardResultEventArgs(result));
        }

        /// <summary>Called by MainWindow when all gallery loaders have finished.</summary>
        public void SetLoadingComplete() => _viewModel.IsLoading = false;

        /// <summary>Displays a dismissible error banner on this page.</summary>
        public void ShowError(string message) => _viewModel.ErrorMessage = message;

        private void OnNavigateNext()
        {
            var nextPage = new VmSettingsPage(_wizardData, _loggerFactory);
            nextPage.WizardCompleted += (s, args) => WizardCompleted?.Invoke(s, args);
            NavigationService.Navigate(nextPage);
        }
    }

    public class WizardResultEventArgs : EventArgs
    {
        public WizardResult Result { get; }
        public WizardResultEventArgs(WizardResult result) => Result = result;
    }
}