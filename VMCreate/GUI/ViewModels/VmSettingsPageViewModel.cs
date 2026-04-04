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
        private int _memoryMB = 4096;
        private int _cpuCount = 2;
        private bool _virtualizationEnabled = true;
        private bool _replacePreviousVm;
        private string _newDriveSizeText;
        private string _validationError;
        private bool _autoDetectDiskSize;

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
            _autoDetectDiskSize = IsDiskImage;

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

        public int MemoryMB
        {
            get => _memoryMB;
            set
            {
                if (SetProperty(ref _memoryMB, value))
                {
                    OnPropertyChanged(nameof(MemoryDisplay));
                    ClearValidationError();
                }
            }
        }

        /// <summary>Human-readable memory label shown next to the slider.</summary>
        public string MemoryDisplay => _memoryMB >= 1024
            ? $"{_memoryMB / 1024.0:0.#} GB"
            : $"{_memoryMB} MB";

        public int CpuCount
        {
            get => _cpuCount;
            set
            {
                if (SetProperty(ref _cpuCount, value))
                {
                    OnPropertyChanged(nameof(CpuDisplay));
                    ClearValidationError();
                }
            }
        }

        /// <summary>Human-readable CPU label shown next to the slider.</summary>
        public string CpuDisplay => _cpuCount == 1 ? "1 core" : $"{_cpuCount} cores";

        public bool VirtualizationEnabled
        {
            get => _virtualizationEnabled;
            set => SetProperty(ref _virtualizationEnabled, value);
        }

        public bool ReplacePreviousVm
        {
            get => _replacePreviousVm;
            set => SetProperty(ref _replacePreviousVm, value);
        }

        public string NewDriveSizeText
        {
            get => _newDriveSizeText;
            set { if (SetProperty(ref _newDriveSizeText, value)) ClearValidationError(); }
        }

        public bool AutoDetectDiskSize
        {
            get => _autoDetectDiskSize;
            set
            {
                if (SetProperty(ref _autoDetectDiskSize, value))
                {
                    OnPropertyChanged(nameof(ShowManualDiskSize));
                    OnPropertyChanged(nameof(ShowAutoSizeHint));
                    ClearValidationError();
                }
            }
        }

        /// <summary>True for disk-based images (VMDK, QCOW2, VHD, OVA) that are not ISO installers and not already VHDX/native.</summary>
        public bool IsDiskImage =>
            IsNotVhdx
            && !string.Equals(_wizardData.SelectedItem?.FileType, "ISO", StringComparison.OrdinalIgnoreCase);

        /// <summary>Show the manual textbox when the user opts out of auto-detection, or for ISO installs.</summary>
        public bool ShowManualDiskSize => IsNotVhdx && !_autoDetectDiskSize;

        /// <summary>Show the auto-size hint when auto-detect is enabled for disk images.</summary>
        public bool ShowAutoSizeHint => IsDiskImage && _autoDetectDiskSize;

        /// <summary>True when the selected image is not in VHDX format and needs conversion.
        /// Native Hyper-V images (e.g. Windows) are already in VHDX format and need no conversion.</summary>
        public bool IsNotVhdx =>
            _wizardData.SelectedItem?.IsNativeHyperV != true
            && !string.Equals(_wizardData.SelectedItem?.FileType, "VHDX", StringComparison.OrdinalIgnoreCase);

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

            // Sliders enforce range, but guard just in case
            if (_memoryMB < 512)
            {
                ValidationError = "Memory must be at least 512 MB!";
                return;
            }

            if (_cpuCount < 1)
            {
                ValidationError = "CPU count must be at least 1!";
                return;
            }

            if (IsNotVhdx)
            {
                if (_autoDetectDiskSize && IsDiskImage)
                {
                    _wizardData.Settings.AutoDetectDiskSize = true;
                }
                else
                {
                    _wizardData.Settings.AutoDetectDiskSize = false;
                    if (!int.TryParse(_newDriveSizeText, out int sizeGB) || sizeGB < 10)
                    {
                        ValidationError = "New Drive Size must be at least 10 GB!";
                        return;
                    }
                    _wizardData.Settings.NewDriveSizeInGB = sizeGB;
                }
            }

            // Apply validated values to WizardData (timestamp is appended in CreateVMAsync)
            _wizardData.Settings.VMName = _vmName.Trim();
            _wizardData.Settings.MemoryInMB = _memoryMB;
            _wizardData.Settings.CPUCount = _cpuCount;
            _wizardData.Settings.VirtualizationEnabled = _virtualizationEnabled;
            _wizardData.Settings.ReplacePreviousVm = _replacePreviousVm;
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
