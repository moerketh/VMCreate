using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using VMCreate.Gallery;

namespace VMCreate
{
    public partial class SelectImagePage : Page
    {
        public event EventHandler<WizardResultEventArgs>? WizardCompleted;

        private readonly SelectImagePageViewModel _viewModel;
        private readonly WizardData _wizardData;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IHtbApiClient _htbApiClient;
        private readonly IEnumerable<IConfigurableCustomizationStep> _configurableSteps;
        private readonly IEnumerable<IDesktopEnvironmentCatalog> _desktopCatalogs;

        public SelectImagePage(WizardData wizardData, IHtbApiClient htbApiClient, IEnumerable<IConfigurableCustomizationStep> configurableSteps, IEnumerable<IDesktopEnvironmentCatalog> desktopCatalogs, ILoggerFactory loggerFactory)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _htbApiClient = htbApiClient ?? throw new ArgumentNullException(nameof(htbApiClient));
            _configurableSteps = configurableSteps ?? throw new ArgumentNullException(nameof(configurableSteps));
            _desktopCatalogs = desktopCatalogs ?? throw new ArgumentNullException(nameof(desktopCatalogs));
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

        /// <summary>
        /// Called by MainWindow with the load summary so the page can show a
        /// live status line ("N images · live from distribution sites · M
        /// loaders failed") instead of silently rendering fewer cards than
        /// the catalog contains.
        /// </summary>
        public void SetGalleryStatus(int itemCount, GalleryLoadSummary summary)
            => _viewModel.SetGalleryStatus(itemCount, summary);

        /// <summary>Displays a dismissible error banner on this page.</summary>
        public void ShowError(string message) => _viewModel.ErrorMessage = message;

        private void OnNavigateNext()
        {
            var nextPage = new VmSettingsPage(_wizardData, _htbApiClient, _configurableSteps, _desktopCatalogs, _loggerFactory);
            nextPage.WizardCompleted += (s, args) => WizardCompleted?.Invoke(s, args);
            NavigationService.Navigate(nextPage);
        }

        /// <summary>Opens the disk image source URL externally instead of navigating the frame.</summary>
        private void DownloadLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _loggerFactory.CreateLogger<SelectImagePage>()
                    .LogDebug(ex, "Failed to open download URL {Uri}", e.Uri);
            }
            e.Handled = true;
        }
    }

    public class WizardResultEventArgs : EventArgs
    {
        public WizardResult Result { get; }
        public WizardResultEventArgs(WizardResult result) => Result = result;
    }
}
