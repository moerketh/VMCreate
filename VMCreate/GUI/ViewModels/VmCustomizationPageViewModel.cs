using Microsoft.Extensions.Logging;
using System;
using System.Windows.Input;

namespace VMCreate
{
    /// <summary>
    /// ViewModel for the VM customization page.
    /// Manages post-install options (e.g. xrdp) and conversion banner visibility.
    /// </summary>
    public class VmCustomizationPageViewModel : ViewModelBase
    {
        private readonly WizardData _wizardData;
        private readonly ILogger _logger;
        private bool _configureXrdp = true;

        /// <summary>Raised when the wizard should complete (Finished or Canceled).</summary>
        public event Action<WizardResult> RequestWizardComplete;

        /// <summary>Raised when the user clicks Back.</summary>
        public event Action RequestNavigateBack;

        public VmCustomizationPageViewModel(WizardData wizardData, ILogger logger)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            FinishCommand = new RelayCommand(OnFinish);
            BackCommand = new RelayCommand(() => RequestNavigateBack?.Invoke());
            CancelCommand = new RelayCommand(() => RequestWizardComplete?.Invoke(WizardResult.Canceled));
        }

        public GalleryItem SelectedItem => _wizardData.SelectedItem;

        public bool ConfigureXrdp
        {
            get => _configureXrdp;
            set => SetProperty(ref _configureXrdp, value);
        }

        /// <summary>True when the selected image is not in VHDX format (requires conversion).</summary>
        public bool IsNotVhdx =>
            !string.Equals(_wizardData.SelectedItem?.FileType, "VHDX", StringComparison.OrdinalIgnoreCase);

        public ICommand FinishCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand CancelCommand { get; }

        private void OnFinish()
        {
            _wizardData.Customizations.ConfigureXrdp = _configureXrdp;
            _logger.LogDebug("Finished customization: ConfigureXrdp={ConfigureXrdp}", _configureXrdp);
            RequestWizardComplete?.Invoke(WizardResult.Finished);
        }
    }
}
