using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using VMCreate.MediaHandlers;

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

        private string _vmName = "";
        private int _memoryMB = 4096;
        private int _cpuCount = 4;
        private bool _virtualizationEnabled = true;
        private bool _replacePreviousVm;
        private string _newDriveSizeText = "";
        private string? _validationError;
        private bool _autoDetectDiskSize;
        private string? _selectedReleaseId;

        /// <summary>Raised when the wizard should complete (e.g. Cancel).</summary>
        public event Action<WizardResult>? RequestWizardComplete;

        /// <summary>Raised when the user clicks Next and validation passes.</summary>
        public event Action? RequestNavigateNext;

        /// <summary>Raised when the user clicks Back.</summary>
        public event Action? RequestNavigateBack;

        public VmSettingsPageViewModel(WizardData wizardData, ILogger logger)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _vmName = _wizardData.SelectedItem?.Name ?? "";
            _newDriveSizeText = _wizardData.Settings.NewDriveSizeInGB.ToString();
            _autoDetectDiskSize = IsDiskImage;

            // Release picker: offered when the distro publishes more than one
            // release (e.g. Kali stable + weekly). The default variant needs
            // no selection — the item's own fields already describe it.
            var variants = _wizardData.SelectedItem?.ReleaseVariants;
            if (variants is { Count: > 1 })
            {
                _selectedReleaseId = _wizardData.SelectedReleaseId
                                      ?? variants.FirstOrDefault(v => v.IsDefault)?.Id
                                      ?? variants[0].Id;
                foreach (var variant in variants)
                {
                    var option = new ReleaseOptionItem(variant);
                    option.IsSelected = option.Id == _selectedReleaseId;
                    ReleaseOptions.Add(option);
                }
            }

            // Restore settings already chosen this session (e.g. after Back navigation or retry).
            _memoryMB = ClampMemory(_wizardData.Settings.MemoryInMB);
            _cpuCount = Math.Clamp(_wizardData.Settings.CPUCount, 1, MaxCpuCount);
            _virtualizationEnabled = _wizardData.Settings.VirtualizationEnabled;
            _replacePreviousVm = _wizardData.Settings.ReplacePreviousVm;

            NextCommand = new RelayCommand(OnNext);
            BackCommand = new RelayCommand(() => RequestNavigateBack?.Invoke());
            CancelCommand = new RelayCommand(() => RequestWizardComplete?.Invoke(WizardResult.Canceled));
            SelectReleaseCommand = new RelayCommand<string>(id => SelectRelease(id));
        }

        public GalleryItem? SelectedItem => _wizardData.SelectedItem;

        /// <summary>Releases offered for the selected image; empty for single-release images.</summary>
        public ObservableCollection<ReleaseOptionItem> ReleaseOptions { get; } = new();

        /// <summary>True when the selected image offers a release choice.</summary>
        public bool ShowReleaseOptions => ReleaseOptions.Count > 1;

        /// <summary>Selection changed on the release radio group.</summary>
        public ICommand SelectReleaseCommand { get; }

        private void SelectRelease(string? id)
        {
            if (string.IsNullOrEmpty(id))
                return;

            _selectedReleaseId = id;
            foreach (var option in ReleaseOptions)
                option.IsSelected = option.Id == id;
        }

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

        // ── Host-aware slider limits ────────────────────────────────────────

        /// <summary>Upper bound of the memory slider: ~75% of host RAM (hard-capped at 64 GB),
        /// or the previous fixed maximum when host memory cannot be detected.</summary>
        public int MaxMemoryMB
        {
            get
            {
                const int fallback = 32768;
                ulong hostMB = HostCapabilities.TotalMemoryMB;
                if (hostMB == 0)
                    return fallback;
                ulong cap = hostMB * 3 / 4;
                return (int)Math.Max(4096, Math.Min(cap, 65536));
            }
        }

        /// <summary>Upper bound of the CPU slider: the host's logical processor count.</summary>
        public int MaxCpuCount => Math.Max(4, HostCapabilities.LogicalProcessors);

        /// <summary>Hint shown under the sliders describing host capability, e.g. "Host: 32 GB RAM / 16 cores".</summary>
        public string HostCapabilityHint
        {
            get
            {
                int cores = HostCapabilities.LogicalProcessors;
                ulong hostMB = HostCapabilities.TotalMemoryMB;
                if (hostMB == 0)
                    return $"Host: {cores} cores";
                return $"Host: {hostMB / 1024.0:0.#} GB RAM / {cores} cores";
            }
        }

        /// <summary>Formatted upper memory bound for the slider's right label.</summary>
        public string MaxMemoryDisplay => MaxMemoryMB >= 1024
            ? $"{MaxMemoryMB / 1024.0:0.#} GB"
            : $"{MaxMemoryMB} MB";

        private int ClampMemory(int mb) => Math.Clamp(mb, 512, MaxMemoryMB);

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
            && DetectFormat() != DiskImageFormat.Iso;

        /// <summary>Show the manual textbox when the user opts out of auto-detection, or for ISO installs.</summary>
        public bool ShowManualDiskSize => IsNotVhdx && !_autoDetectDiskSize;

        /// <summary>Show the auto-size hint when auto-detect is enabled for disk images.</summary>
        public bool ShowAutoSizeHint => IsDiskImage && _autoDetectDiskSize;

        /// <summary>True when the selected image is not in VHDX format and needs conversion.
        /// Native Hyper-V images (e.g. Windows) are already in VHDX format and need no conversion.</summary>
        public bool IsNotVhdx =>
            _wizardData.SelectedItem?.IsNativeHyperV != true
            && DetectFormat() != DiskImageFormat.Vhdx;

        private DiskImageFormat DetectFormat()
            => DiskFileDetector.DetectFileType(_wizardData.SelectedItem?.DiskUri);

        public string? ValidationError
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

            // Sliders enforce range, but guard against out-of-host-range values
            if (_memoryMB < 512)
            {
                ValidationError = "Memory must be at least 512 MB!";
                return;
            }

            if (_memoryMB > MaxMemoryMB)
            {
                ValidationError = $"Memory exceeds what this host can provide (max {MaxMemoryDisplay}).";
                return;
            }

            if (_cpuCount < 1)
            {
                ValidationError = "CPU count must be at least 1!";
                return;
            }

            if (_cpuCount > MaxCpuCount)
            {
                ValidationError = $"CPU count exceeds this host's {HostCapabilities.LogicalProcessors} logical processors.";
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
            _wizardData.Settings.EnhancedSessionTransportType = _wizardData.SelectedItem?.EnhancedSessionTransportType;

            // Persist the release choice; the default variant normalizes to
            // null so the wizard data always describes the item itself.
            var defaultReleaseId = _wizardData.SelectedItem?.ReleaseVariants?
                .FirstOrDefault(v => v.IsDefault)?.Id;
            _wizardData.SelectedReleaseId = _selectedReleaseId != null
                                            && !string.Equals(_selectedReleaseId, defaultReleaseId,
                                                StringComparison.OrdinalIgnoreCase)
                ? _selectedReleaseId
                : null;

            ValidationError = null;

            _logger.LogDebug(
                "Validated VM settings: VMName={VMName}, Memory={Memory}MB, CPU={CPU}, VirtualizationEnabled={VirtualizationEnabled}, NewDriveSizeGB={NewDriveSizeGB}",
                _wizardData.Settings.VMName, _wizardData.Settings.MemoryInMB, _wizardData.Settings.CPUCount,
                _wizardData.Settings.VirtualizationEnabled, _wizardData.Settings.NewDriveSizeInGB);

            RequestNavigateNext?.Invoke();
        }
    }
}
