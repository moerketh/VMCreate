using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace VMCreate
{
    /// <summary>
    /// ViewModel for the Deploy page. Pre-builds phase cards from the selected
    /// gallery item and VM settings, then updates them as progress reports arrive.
    /// </summary>
    public class DeployPageViewModel : ViewModelBase
    {
        // Well-known phase IDs (must match the strings reported in CreateVMProgressInfo.Phase)
        public const string PhaseDownload   = "Download";
        public const string PhaseExtract    = "Extract";
        public const string PhaseConvert    = "Convert";
        public const string PhaseCreateVM   = "CreateVM";
        public const string PhaseStartVM    = "StartVM";
        public const string PhaseCloneDisk  = "CloneDisk";
        public const string PhaseCustomize  = "Customize";
        public const string PhasePostBoot   = "PostBoot";
        public const string PhaseDone       = "Done";

        private readonly ILogger _logger;
        private bool _isDeploying;
        private bool _isComplete;
        private bool _hasFailed;
        private string _vmName;
        private string _errorMessage;

        public event Action<WizardResult> RequestWizardComplete;
        public event Action RequestCancel;

        public DeployPageViewModel(WizardData wizardData, ILogger logger)
        {
            if (wizardData == null) throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            VmName = wizardData.Settings?.VMName ?? "VM";
            SelectedItem = wizardData.SelectedItem;

            CancelCommand = new RelayCommand(OnCancel, () => _isDeploying && !_isComplete);
            ConnectToVmCommand = new RelayCommand(OnConnectToVm);
            OpenHyperVManagerCommand = new RelayCommand(OnOpenHyperVManager);
            NewVmCommand = new RelayCommand(() => RequestWizardComplete?.Invoke(WizardResult.Finished));

            BuildPhaseList(wizardData);
        }

        public ObservableCollection<DeploymentPhase> Phases { get; } = new ObservableCollection<DeploymentPhase>();

        public GalleryItem SelectedItem { get; }

        public bool IsDeploying
        {
            get => _isDeploying;
            set
            {
                if (SetProperty(ref _isDeploying, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsComplete
        {
            get => _isComplete;
            set
            {
                if (SetProperty(ref _isComplete, value))
                {
                    OnPropertyChanged(nameof(IsFinished));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool HasFailed
        {
            get => _hasFailed;
            set
            {
                if (SetProperty(ref _hasFailed, value))
                {
                    OnPropertyChanged(nameof(IsFinished));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>True when deployment ended (success or failure). Used to show post-action buttons.</summary>
        public bool IsFinished => _isComplete || _hasFailed;

        public string VmName
        {
            get => _vmName;
            set => SetProperty(ref _vmName, value);
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (SetProperty(ref _errorMessage, value))
                    OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_errorMessage);

        public ICommand CancelCommand { get; }
        public ICommand ConnectToVmCommand { get; }
        public ICommand OpenHyperVManagerCommand { get; }
        public ICommand NewVmCommand { get; }

        // ── Phase list construction ──────────────────────────────────────

        private void BuildPhaseList(WizardData wizardData)
        {
            string fileType = wizardData.SelectedItem?.FileType ?? "Unknown";
            bool needsExtraction = fileType is not ("ISO" or "QCOW2" or "VHDX" or "VHD");
            bool needsConversion = fileType is "VMDK" or "QCOW2" or "OVA" or "Archive";
            bool nestedVirt = wizardData.Settings?.VirtualizationEnabled ?? true;

            Phases.Add(new DeploymentPhase(PhaseDownload, "Download",
                "Downloading disk image from the internet",
                SymbolRegular.ArrowDownload24));

            if (needsExtraction)
            {
                Phases.Add(new DeploymentPhase(PhaseExtract, "Extract",
                    "Extracting disk image from archive",
                    SymbolRegular.FolderOpen24));
            }

            if (needsConversion)
            {
                Phases.Add(new DeploymentPhase(PhaseConvert, "Convert to VHDX",
                    "Converting disk image to Hyper-V format",
                    SymbolRegular.ArrowSync24));
            }

            Phases.Add(new DeploymentPhase(PhaseCreateVM, "Create VM",
                "Creating and configuring the Hyper-V virtual machine",
                SymbolRegular.Desktop24));

            Phases.Add(new DeploymentPhase(PhaseStartVM, "Start VM",
                nestedVirt
                    ? "Enabling nested virtualization and starting the VM"
                    : "Starting the virtual machine",
                SymbolRegular.Play24));

            // Show pre-boot customization card upfront if any pre-boot option was selected
            if (wizardData.Customizations?.HasPreBootCustomizations == true)
            {
                Phases.Add(new DeploymentPhase(PhaseCustomize, "Pre-Boot Customizations",
                    "Applying customizations (xRDP, enhancements) and waiting for the VM to restart",
                    SymbolRegular.Wrench24));
            }

            // Always show the post-boot card — RemoveVBoxGuestAdditionsStep runs
            // unconditionally, and user-selected options (timezone, VPN) add to it.
            Phases.Add(new DeploymentPhase(PhasePostBoot, "Post-Boot Config",
                wizardData.Customizations != null
                    ? BuildPostBootDescription(wizardData.Customizations)
                    : "Applying post-boot customizations via SSH",
                SymbolRegular.Settings24));

            Phases.Add(new DeploymentPhase(PhaseDone, "Done",
                "Virtual machine created successfully!",
                SymbolRegular.CheckmarkCircle24));
        }

        /// <summary>
        /// Called at runtime when partition detection reveals an MBR disk.
        /// Inserts the clone + customize cards before PostBoot (or Done if no PostBoot).
        /// </summary>
        public void InsertMbrPhases()
        {
            // Only insert once
            if (Phases.Any(p => p.Id == PhaseCloneDisk)) return;

            int insertIndex = FindInsertIndexBeforePostBootOrDone();
            if (insertIndex < 0) return;

            // Insert Customize (parent) first if not already present,
            // then CloneDisk as an indented sub-step underneath it.
            if (!Phases.Any(p => p.Id == PhaseCustomize))
            {
                Phases.Insert(insertIndex, new DeploymentPhase(PhaseCustomize, "Pre-Boot Customizations",
                    "Applying customizations and waiting for the VM to shut down",
                    SymbolRegular.Wrench24));
                insertIndex++; // CloneDisk goes after Customize
            }
            else
            {
                // Customize already present — insert CloneDisk right after it
                for (int i = 0; i < Phases.Count; i++)
                {
                    if (Phases[i].Id == PhaseCustomize) { insertIndex = i + 1; break; }
                }
            }

            Phases.Insert(insertIndex, new DeploymentPhase(PhaseCloneDisk, "Clone Disk",
                "Cloning MBR disk to GPT format inside the VM",
                SymbolRegular.HardDrive24) { IndentLevel = 1 });
        }

        /// <summary>
        /// Called at runtime for Gen2 pre-installed images that need customization
        /// (e.g. xRDP install). Inserts a Customize card before PostBoot (or Done if no PostBoot).
        /// </summary>
        public void InsertCustomizePhase()
        {
            // Only insert once
            if (Phases.Any(p => p.Id == PhaseCustomize)) return;

            int insertIndex = FindInsertIndexBeforePostBootOrDone();
            if (insertIndex < 0) return;

            Phases.Insert(insertIndex, new DeploymentPhase(PhaseCustomize, "Pre-Boot Customizations",
                "Installing Hyper-V enhancements and waiting for the VM to restart",
                SymbolRegular.Wrench24));
        }

        /// <summary>
        /// Finds the index to insert dynamic phases (Customize, CloneDisk) so they
        /// appear before PostBoot. Falls back to before Done if no PostBoot card exists.
        /// </summary>
        private int FindInsertIndexBeforePostBootOrDone()
        {
            for (int i = 0; i < Phases.Count; i++)
            {
                if (Phases[i].Id == PhasePostBoot || Phases[i].Id == PhaseDone)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Called at runtime when post-boot customization options are enabled
        /// (e.g. HTB VPN, timezone sync). Inserts a PostBoot card before Done.
        /// Only needed if the card wasn't already added by BuildPhaseList.
        /// </summary>
        public void InsertPostBootPhase()
        {
            // Only insert once — already present if customizations were selected in the wizard
            if (Phases.Any(p => p.Id == PhasePostBoot)) return;

            int doneIndex = -1;
            for (int i = 0; i < Phases.Count; i++)
            {
                if (Phases[i].Id == PhaseDone) { doneIndex = i; break; }
            }
            if (doneIndex < 0) return;

            Phases.Insert(doneIndex, new DeploymentPhase(PhasePostBoot, "Post-Boot Config",
                "Applying post-boot customizations via SSH",
                SymbolRegular.Settings24));
        }

        /// <summary>Builds a descriptive string for the post-boot card based on what's enabled.</summary>
        private static string BuildPostBootDescription(VmCustomizations c)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (c.SyncTimezone) parts.Add("timezone sync");
            if (c.ConfigureHtbVpn) parts.Add("HTB VPN deployment");
            return parts.Count > 0
                ? "SSH into VM to apply: " + string.Join(", ", parts)
                : "Applying post-boot customizations via SSH";
        }

        // ── Phase status updates ─────────────────────────────────────────

        public DeploymentPhase FindPhase(string id) =>
            Phases.FirstOrDefault(p => p.Id == id);

        public void ActivatePhase(string id)
        {
            var phase = FindPhase(id);
            if (phase == null) return;

            // When activating a sub-step, also activate its parent phase
            // so the UI shows both as in-progress (e.g. "Clone Disk" under "Pre-Boot Customizations").
            if (phase.IndentLevel > 0)
            {
                int idx = Phases.IndexOf(phase);
                for (int i = idx - 1; i >= 0; i--)
                {
                    if (Phases[i].IndentLevel < phase.IndentLevel)
                    {
                        if (Phases[i].Status == DeploymentPhaseStatus.Pending)
                        {
                            Phases[i].Status = DeploymentPhaseStatus.Active;
                            Phases[i].IsIndeterminate = true;
                        }
                        break;
                    }
                }
            }

            phase.Status = DeploymentPhaseStatus.Active;
            phase.IsIndeterminate = true;
            _logger.LogDebug("Phase activated: {Phase}", id);
        }

        public void CompletePhase(string id)
        {
            var phase = FindPhase(id);
            if (phase == null) return;
            phase.Status = DeploymentPhaseStatus.Completed;
            phase.ProgressPercentage = 100;
            phase.IsIndeterminate = false;
            phase.ProgressText = null;
            _logger.LogDebug("Phase completed: {Phase}", id);
        }

        public void FailPhase(string id, string message)
        {
            var phase = FindPhase(id);
            if (phase == null) return;
            phase.Status = DeploymentPhaseStatus.Failed;
            phase.IsIndeterminate = false;
            phase.ProgressText = message;
        }

        public void UpdatePhaseProgress(string id, int percentage, string text = null)
        {
            var phase = FindPhase(id);
            if (phase == null) return;
            if (phase.Status != DeploymentPhaseStatus.Active)
                phase.Status = DeploymentPhaseStatus.Active;
            phase.IsIndeterminate = false;
            phase.ProgressPercentage = percentage;
            if (text != null)
                phase.ProgressText = text;
        }

        // ── Commands ─────────────────────────────────────────────────────

        private void OnCancel()
        {
            RequestCancel?.Invoke();
        }

        private void OnConnectToVm()
        {
            try
            {
                Process.Start(new ProcessStartInfo("vmconnect.exe", $"localhost \"{_vmName}\"")
                { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch vmconnect.exe");
            }
        }

        private void OnOpenHyperVManager()
        {
            try
            {
                Process.Start(new ProcessStartInfo("mmc.exe", "virtmgmt.msc")
                { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch Hyper-V Manager");
            }
        }
    }
}
