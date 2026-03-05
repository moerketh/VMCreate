using Microsoft.Extensions.Logging;
using System;
using System.Windows.Input;

namespace VMCreate
{
    /// <summary>
    /// ViewModel for the VM settings page.
    /// Manages form fields, validation, and the conversion banner visibility.
    /// </summary>
    public class VmSettingsPageViewModel : ViewModelBase
    {
        private readonly WizardData _wizardData;
        private readonly ILogger _logger;

        private string _vmName;
        private string _memoryText = "4096";
        private string _cpuText = "2";
        private bool _virtualizationEnabled = true;
        private string _newDriveSizeText;
        private string _validationError;

        /// <summary>Raised when the wizard should complete (e.g. Cancel).</summary>
        public event Action<WizardResult> RequestWizardComplete;

        /// <summary>Raised when the user clicks Next and validation passes.</summary>
        public event Action RequestNavigateNext;

        /// <summary>Raised when the user clicks Back.</summary>
        public event Action RequestNavigateBack;

        public VmSettingsPageViewModel(WizardData wizardData, ILogger logger)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _vmName = _wizardData.SelectedItem?.Name ?? "";
            _newDriveSizeText = _wizardData.Settings.NewDriveSizeInGB.ToString();

            NextCommand = new RelayCommand(OnNext);
            BackCommand = new RelayCommand(() => RequestNavigateBack?.Invoke());
            CancelCommand = new RelayCommand(() => RequestWizardComplete?.Invoke(WizardResult.Canceled));
        }

        public GalleryItem SelectedItem => _wizardData.SelectedItem;

        public string VmName
        {
            get => _vmName;
            set { if (SetProperty(ref _vmName, value)) ClearValidationError(); }
        }

        public string MemoryText
        {
            get => _memoryText;
            set { if (SetProperty(ref _memoryText, value)) ClearValidationError(); }
        }

        public string CpuText
        {
            get => _cpuText;
            set { if (SetProperty(ref _cpuText, value)) ClearValidationError(); }
        }

        public bool VirtualizationEnabled
        {
            get => _virtualizationEnabled;
            set => SetProperty(ref _virtualizationEnabled, value);
        }

        public string NewDriveSizeText
        {
            get => _newDriveSizeText;
            set { if (SetProperty(ref _newDriveSizeText, value)) ClearValidationError(); }
        }

        /// <summary>True when the selected image is not in VHDX format (requires conversion).</summary>
        public bool IsNotVhdx =>
            !string.Equals(_wizardData.SelectedItem?.FileType, "VHDX", StringComparison.OrdinalIgnoreCase);

        public string ValidationError
        {
            get => _validationError;
            private set
            {
                if (SetProperty(ref _validationError, value))
                    OnPropertyChanged(nameof(HasValidationError));
            }
        }

        public bool HasValidationError => !string.IsNullOrEmpty(_validationError);

        public ICommand NextCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand CancelCommand { get; }

        private void ClearValidationError()
        {
            if (_validationError != null)
                ValidationError = null;
        }

        private void OnNext()
        {
            if (string.IsNullOrWhiteSpace(_vmName))
            {
                ValidationError = "VM Name is required!";
                return;
            }

            if (!int.TryParse(_memoryText, out int memoryMB) || memoryMB < 512)
            {
                ValidationError = "Memory must be at least 512 MB!";
                return;
            }

            if (!int.TryParse(_cpuText, out int cpuCount) || cpuCount < 1)
            {
                ValidationError = "CPU count must be at least 1!";
                return;
            }

            if (IsNotVhdx)
            {
                if (!int.TryParse(_newDriveSizeText, out int sizeGB) || sizeGB < 10)
                {
                    ValidationError = "New Drive Size must be at least 10 GB!";
                    return;
                }
                _wizardData.Settings.NewDriveSizeInGB = sizeGB;
            }

            // Apply validated values to WizardData
            _wizardData.Settings.VMName = $"{_vmName.Trim()}_{DateTime.Now:yyyyMMddHHmmss}";
            _wizardData.Settings.MemoryInMB = memoryMB;
            _wizardData.Settings.CPUCount = cpuCount;
            _wizardData.Settings.VirtualizationEnabled = _virtualizationEnabled;
            _wizardData.Settings.EnhancedSessionTransportType = _wizardData.SelectedItem.EnhancedSessionTransportType;

            ValidationError = null;

            _logger.LogDebug(
                "Validated VM settings: VMName={VMName}, Memory={Memory}MB, CPU={CPU}, VirtualizationEnabled={VirtualizationEnabled}, NewDriveSizeGB={NewDriveSizeGB}",
                _wizardData.Settings.VMName, _wizardData.Settings.MemoryInMB, _wizardData.Settings.CPUCount,
                _wizardData.Settings.VirtualizationEnabled, _wizardData.Settings.NewDriveSizeInGB);

            RequestNavigateNext?.Invoke();
        }
    }
}
