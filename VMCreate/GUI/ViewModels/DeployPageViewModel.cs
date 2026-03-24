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
        public const string PhaseDownloadCloningIso = "DownloadCloningIso";
        public const string PhaseCreateVM   = "CreateVM";
        public const string PhaseStartVM    = "StartVM";
        public const string PhaseCloneDisk  = "CloneDisk";
        public const string PhaseCustomize  = "Customize";
        public const string PhasePostBoot   = "PostBoot";
        public const string PhaseDone       = "Done";

        // Pre-boot sub-step IDs (matched to KVP WorkflowProgress prefixes)
        public const string SubInstallGrub   = "Sub_InstallGrub";
        public const string SubInstallHyperV = "Sub_InstallHyperV";
        public const string SubInstallXrdp   = "Sub_InstallXrdp";
        public const string SubInstallPwsh   = "Sub_InstallPwsh";
        public const string SubSshSetup      = "Sub_SshSetup";
        public const string SubReboot        = "Sub_Reboot";

        // CreateVM sub-step IDs (reported via CreateVMProgressInfo.SubStep)
        public const string SubCreateVMSkeleton  = "Sub_CreateVMSkeleton";
        public const string SubConnectNic        = "Sub_ConnectNic";
        public const string SubConfigureHardware = "Sub_ConfigureHardware";
        public const string SubAttachDisk        = "Sub_AttachDisk";
        public const string SubAttachCloneDisk   = "Sub_AttachCloneDisk";
        public const string SubAttachBootDvd     = "Sub_AttachBootDvd";
        public const string SubSetBootOrder      = "Sub_SetBootOrder";
        public const string SubEnableNestedVirt  = "Sub_EnableNestedVirt";

        // Post-ISO-cycle cleanup sub-step ID
        public const string SubCleanupIsoBoot = "Sub_CleanupIsoBoot";

        // Post-boot infrastructure sub-step IDs
        public const string SubAddTempNic   = "Sub_AddTempNic";
        public const string SubWaitForSsh   = "Sub_WaitForSsh";

        // Post-boot sub-step IDs (matched to ICustomizationStep.Name)
        public const string SubRemoveVBox    = "Sub_RemoveVBox";
        public const string SubSyncTimezone  = "Sub_SyncTimezone";
        public const string SubConfigureVpn  = "Sub_ConfigureVpn";
        public const string SubRestoreSsh    = "Sub_RestoreSsh";

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
            _lastCustomizations = wizardData.Customizations;
            _lastSettings = wizardData.Settings;

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
            bool isNativeHyperV = wizardData.SelectedItem?.IsNativeHyperV == true;
            bool needsExtraction = fileType is not ("ISO" or "QCOW2" or "VHDX" or "VHD");
            bool needsConversion = !isNativeHyperV
                && fileType is "VMDK" or "QCOW2" or "OVA" or "Archive";
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
            AddCreateVMSubSteps(wizardData);

            Phases.Add(new DeploymentPhase(PhaseStartVM, "Start VM",
                nestedVirt
                    ? "Enabling nested virtualization and starting the VM"
                    : "Starting the virtual machine",
                SymbolRegular.Play24));

            if (!isNativeHyperV)
            {
                // Show pre-boot customization card upfront if any pre-boot option was selected
                if (wizardData.Customizations?.HasPreBootCustomizations == true)
                {
                    Phases.Add(new DeploymentPhase(PhaseCustomize, "Pre-Boot Customizations",
                        "Applying customizations (xRDP, enhancements) and waiting for the VM to restart",
                        SymbolRegular.Wrench24));
                    AddPreBootSubSteps(wizardData.Customizations);
                }

                // Always show the post-boot card — RemoveVBoxGuestAdditionsStep runs
                // unconditionally, and user-selected options (timezone, VPN) add to it.
                Phases.Add(new DeploymentPhase(PhasePostBoot, "Post-Boot Config",
                    wizardData.Customizations != null
                        ? BuildPostBootDescription(wizardData.Customizations)
                        : "Applying post-boot customizations via SSH",
                    SymbolRegular.Settings24));
                AddPostBootSubSteps(wizardData.Customizations);
            }

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
            // then CloneDisk as the first sub-step (it runs before the other customizations).
            if (!Phases.Any(p => p.Id == PhaseCustomize))
            {
                Phases.Insert(insertIndex, new DeploymentPhase(PhaseCustomize, "Pre-Boot Customizations",
                    "Applying customizations and waiting for the VM to shut down",
                    SymbolRegular.Wrench24));
                insertIndex++;

                // Clone Disk runs first in the Gen1 MBR flow
                Phases.Insert(insertIndex, new DeploymentPhase(PhaseCloneDisk, "Clone Disk",
                    "Cloning MBR disk to GPT format inside the VM",
                    SymbolRegular.HardDrive24) { IndentLevel = 1, IsVisible = false });
                insertIndex++;

                // GRUB install follows clone (lengthy step)
                Phases.Insert(insertIndex, new DeploymentPhase(SubInstallGrub, "Install GRUB",
                    "Installing GRUB bootloader for UEFI boot",
                    SymbolRegular.ArrowSync24) { IndentLevel = 1, IsVisible = false });
                insertIndex++;

                // Then the remaining pre-boot sub-steps
                InsertPreBootSubStepsAt(insertIndex, _lastCustomizations);
            }
            else
            {
                // Customize already present — insert CloneDisk right after parent, before other sub-steps
                int customizeIdx = -1;
                for (int i = 0; i < Phases.Count; i++)
                {
                    if (Phases[i].Id == PhaseCustomize) { customizeIdx = i; break; }
                }
                if (customizeIdx >= 0)
                {
                    int insertAt = customizeIdx + 1;
                    Phases.Insert(insertAt++, new DeploymentPhase(PhaseCloneDisk, "Clone Disk",
                        "Cloning MBR disk to GPT format inside the VM",
                        SymbolRegular.HardDrive24) { IndentLevel = 1, IsVisible = false });
                    Phases.Insert(insertAt, new DeploymentPhase(SubInstallGrub, "Install GRUB",
                        "Installing GRUB bootloader for UEFI boot",
                        SymbolRegular.ArrowSync24) { IndentLevel = 1, IsVisible = false });
                }
            }
        }

        /// <summary>
        /// Called at runtime for Gen2 pre-installed images that need customization
        /// (e.g. xRDP install). Inserts a Customize card before PostBoot (or Done if no PostBoot).
        /// </summary>
        /// <summary>
        /// Inserts a "Download Cloning ISO" card before CreateVM.
        /// Called dynamically when the cloning ISO download phase fires.
        /// </summary>
        public void InsertDownloadCloningIsoPhase()
        {
            if (Phases.Any(p => p.Id == PhaseDownloadCloningIso)) return;

            int createVmIndex = -1;
            for (int i = 0; i < Phases.Count; i++)
            {
                if (Phases[i].Id == PhaseCreateVM) { createVmIndex = i; break; }
            }
            if (createVmIndex < 0) return;

            Phases.Insert(createVmIndex, new DeploymentPhase(PhaseDownloadCloningIso, "Download Cloning ISO",
                "Downloading the cloning ISO for VM customization",
                SymbolRegular.ArrowDownload24));
        }

        public void InsertCustomizePhase()
        {
            // Only insert once
            if (Phases.Any(p => p.Id == PhaseCustomize)) return;

            int insertIndex = FindInsertIndexBeforePostBootOrDone();
            if (insertIndex < 0) return;

            Phases.Insert(insertIndex, new DeploymentPhase(PhaseCustomize, "Pre-Boot Customizations",
                "Installing Hyper-V enhancements and waiting for the VM to restart",
                SymbolRegular.Wrench24));
            insertIndex++;
            InsertPreBootSubStepsAt(insertIndex, _lastCustomizations);
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
            InsertPostBootSubStepsAt(doneIndex + 1, _lastCustomizations);
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

        // ── Sub-step helpers ─────────────────────────────────────────────

        /// <summary>Stashed customizations so dynamic Insert* methods can add the right sub-steps.</summary>
        private VmCustomizations _lastCustomizations;

        /// <summary>Stashed settings so dynamic Insert* methods can add conditional sub-steps.</summary>
        private VmSettings _lastSettings;

        /// <summary>Appends CreateVM sub-step cards (IndentLevel=1, hidden) after the CreateVM parent phase.</summary>
        private void AddCreateVMSubSteps(WizardData wizardData)
        {
            bool nestedVirt = wizardData.Settings?.VirtualizationEnabled ?? true;
            bool isIso = (wizardData.SelectedItem?.FileType ?? "Unknown") == "ISO";

            Phases.Add(new DeploymentPhase(SubCreateVMSkeleton, "Create Hyper-V VM",
                "Creating the virtual machine shell", SymbolRegular.Desktop24) { IndentLevel = 1, IsVisible = false });
            Phases.Add(new DeploymentPhase(SubConnectNic, "Connect Network Adapter",
                "Connecting VM to Default Switch", SymbolRegular.PlugConnected24) { IndentLevel = 1, IsVisible = false });
            Phases.Add(new DeploymentPhase(SubConfigureHardware, "Configure Hardware",
                "Setting CPU count, memory, secure boot, and integration services", SymbolRegular.Board24) { IndentLevel = 1, IsVisible = false });
            Phases.Add(new DeploymentPhase(SubAttachDisk, isIso ? "Create Boot Disk" : "Attach Disk",
                isIso ? "Creating empty VHDX for the OS installer" : "Attaching converted disk image",
                SymbolRegular.HardDrive24) { IndentLevel = 1, IsVisible = false });
            // AttachCloneDisk and AttachBootDvd are inserted dynamically when DetectedGeneration arrives,
            // or statically for ISO flows
            if (isIso)
            {
                Phases.Add(new DeploymentPhase(SubAttachBootDvd, "Attach Installer ISO",
                    "Mounting ISO image as DVD drive", SymbolRegular.Storage24) { IndentLevel = 1, IsVisible = false });
            }
            Phases.Add(new DeploymentPhase(SubSetBootOrder, "Set Boot Order",
                "Configuring boot device priority", SymbolRegular.ArrowSort24) { IndentLevel = 1, IsVisible = false });
            if (nestedVirt)
            {
                Phases.Add(new DeploymentPhase(SubEnableNestedVirt, "Enable Nested Virtualization",
                    "Exposing virtualization extensions to the guest", SymbolRegular.LayerDiagonal24) { IndentLevel = 1, IsVisible = false });
            }
        }

        /// <summary>
        /// Called at runtime when DetectedGeneration arrives. Inserts the AttachCloneDisk
        /// (Gen1 only) and AttachBootDvd cards into the CreateVM sub-step list.
        /// </summary>
        public void InsertDiskSubSteps(int detectedGeneration, bool needsIsoBoot)
        {
            // Find AttachDisk card index — insert Clone/DVD after it
            int attachDiskIdx = -1;
            for (int i = 0; i < Phases.Count; i++)
            {
                if (Phases[i].Id == SubAttachDisk) { attachDiskIdx = i; break; }
            }
            if (attachDiskIdx < 0) return;

            int insertAt = attachDiskIdx + 1;

            if (detectedGeneration == 1 && !Phases.Any(p => p.Id == SubAttachCloneDisk))
            {
                Phases.Insert(insertAt++, new DeploymentPhase(SubAttachCloneDisk, "Attach Source Disk",
                    "Attaching original MBR disk as secondary for cloning",
                    SymbolRegular.HardDrive24) { IndentLevel = 1, IsVisible = false });
            }

            if (needsIsoBoot && !Phases.Any(p => p.Id == SubAttachBootDvd))
            {
                Phases.Insert(insertAt, new DeploymentPhase(SubAttachBootDvd, "Attach Boot ISO",
                    "Mounting customization ISO as DVD drive",
                    SymbolRegular.Storage24) { IndentLevel = 1, IsVisible = false });
            }
        }

        /// <summary>
        /// Dynamically inserts a cleanup card between Pre-Boot Customizations and PostBoot/Done.
        /// Called after the ISO boot cycle completes.
        /// </summary>
        public void InsertCleanupIsoBootPhase()
        {
            if (Phases.Any(p => p.Id == SubCleanupIsoBoot)) return;

            int insertIndex = FindInsertIndexBeforePostBootOrDone();
            if (insertIndex < 0) return;

            Phases.Insert(insertIndex, new DeploymentPhase(SubCleanupIsoBoot, "Cleanup Boot Media",
                "Removing ISO and source disk, setting boot to hard drive",
                SymbolRegular.Broom24) { IndentLevel = 0 });
        }

        /// <summary>Appends pre-boot sub-step cards (IndentLevel=1, hidden) to the end of Phases.</summary>
        private void AddPreBootSubSteps(VmCustomizations c)
        {
            Phases.Add(new DeploymentPhase(SubInstallHyperV, "Install Hyper-V packages",
                "Installing guest integration services", SymbolRegular.Box24) { IndentLevel = 1, IsVisible = false });
            if (c?.ConfigureXrdp == true)
            {
                Phases.Add(new DeploymentPhase(SubInstallXrdp, "Install xRDP",
                    "Installing xRDP for Enhanced Session support", SymbolRegular.Desktop24) { IndentLevel = 1, IsVisible = false });
            }
            Phases.Add(new DeploymentPhase(SubInstallPwsh, "Install PowerShell",
                "Installing PowerShell for post-boot management", SymbolRegular.Code24) { IndentLevel = 1, IsVisible = false });
            Phases.Add(new DeploymentPhase(SubSshSetup, "SSH setup",
                "Creating automation user and injecting SSH key", SymbolRegular.Key24) { IndentLevel = 1, IsVisible = false });
            Phases.Add(new DeploymentPhase(SubReboot, "Reboot",
                "Shutting down VM to boot from converted disk", SymbolRegular.Power24) { IndentLevel = 1, IsVisible = false });
        }

        /// <summary>Inserts pre-boot sub-step cards at a given index (hidden). Returns the next free index.</summary>
        private int InsertPreBootSubStepsAt(int index, VmCustomizations c)
        {
            Phases.Insert(index++, new DeploymentPhase(SubInstallHyperV, "Install Hyper-V packages",
                "Installing guest integration services", SymbolRegular.Box24) { IndentLevel = 1, IsVisible = false });
            if (c?.ConfigureXrdp == true)
            {
                Phases.Insert(index++, new DeploymentPhase(SubInstallXrdp, "Install xRDP",
                    "Installing xRDP for Enhanced Session support", SymbolRegular.Desktop24) { IndentLevel = 1, IsVisible = false });
            }
            Phases.Insert(index++, new DeploymentPhase(SubInstallPwsh, "Install PowerShell",
                "Installing PowerShell for post-boot management", SymbolRegular.Code24) { IndentLevel = 1, IsVisible = false });
            Phases.Insert(index++, new DeploymentPhase(SubSshSetup, "SSH setup",
                "Creating automation user and injecting SSH key", SymbolRegular.Key24) { IndentLevel = 1, IsVisible = false });
            Phases.Insert(index++, new DeploymentPhase(SubReboot, "Reboot",
                "Shutting down VM to boot from converted disk", SymbolRegular.Power24) { IndentLevel = 1, IsVisible = false });
            return index;
        }

        /// <summary>Appends post-boot sub-step cards (IndentLevel=1, hidden) to the end of Phases.</summary>
        private void AddPostBootSubSteps(VmCustomizations c)
        {
            Phases.Add(new DeploymentPhase(SubAddTempNic, "Add Temporary NIC",
                "Adding temporary network adapter for SSH access", SymbolRegular.PlugConnected24) { IndentLevel = 1, IsVisible = false });
            Phases.Add(new DeploymentPhase(SubWaitForSsh, "Waiting for SSH",
                "Waiting for the VM to accept SSH connections", SymbolRegular.PlugConnected24) { IndentLevel = 1, IsVisible = false });
            Phases.Add(new DeploymentPhase(SubRemoveVBox, "Remove VBox Guest Additions",
                "Cleaning up VirtualBox artifacts", SymbolRegular.Delete24) { IndentLevel = 1, IsVisible = false });
            if (c?.SyncTimezone == true)
            {
                Phases.Add(new DeploymentPhase(SubSyncTimezone, "Sync Timezone",
                    "Setting guest timezone to match host", SymbolRegular.Clock24) { IndentLevel = 1, IsVisible = false });
            }
            if (c?.ConfigureHtbVpn == true)
            {
                Phases.Add(new DeploymentPhase(SubConfigureVpn, "Configure VPN",
                    "Installing OpenVPN and deploying VPN configs", SymbolRegular.Globe24) { IndentLevel = 1, IsVisible = false });
            }
            Phases.Add(new DeploymentPhase(SubRestoreSsh, "Restore SSH State",
                "Restoring the original SSH configuration", SymbolRegular.ShieldKeyhole24) { IndentLevel = 1, IsVisible = false });
        }

        /// <summary>Inserts post-boot sub-step cards at a given index (hidden). Returns the next free index.</summary>
        private int InsertPostBootSubStepsAt(int index, VmCustomizations c)
        {
            Phases.Insert(index++, new DeploymentPhase(SubAddTempNic, "Add Temporary NIC",
                "Adding temporary network adapter for SSH access", SymbolRegular.PlugConnected24) { IndentLevel = 1, IsVisible = false });
            Phases.Insert(index++, new DeploymentPhase(SubWaitForSsh, "Waiting for SSH",
                "Waiting for the VM to accept SSH connections", SymbolRegular.PlugConnected24) { IndentLevel = 1, IsVisible = false });
            Phases.Insert(index++, new DeploymentPhase(SubRemoveVBox, "Remove VBox Guest Additions",
                "Cleaning up VirtualBox artifacts", SymbolRegular.Delete24) { IndentLevel = 1, IsVisible = false });
            if (c?.SyncTimezone == true)
            {
                Phases.Insert(index++, new DeploymentPhase(SubSyncTimezone, "Sync Timezone",
                    "Setting guest timezone to match host", SymbolRegular.Clock24) { IndentLevel = 1, IsVisible = false });
            }
            if (c?.ConfigureHtbVpn == true)
            {
                Phases.Insert(index++, new DeploymentPhase(SubConfigureVpn, "Configure VPN",
                    "Installing OpenVPN and deploying VPN configs", SymbolRegular.Globe24) { IndentLevel = 1, IsVisible = false });
            }
            Phases.Insert(index++, new DeploymentPhase(SubRestoreSsh, "Restore SSH State",
                "Restoring the original SSH configuration", SymbolRegular.ShieldKeyhole24) { IndentLevel = 1, IsVisible = false });
            return index;
        }

        /// <summary>Finds the index after the last IndentLevel>0 child of the Customize phase.</summary>
        private int FindEndOfCustomizeSubSteps()
        {
            int customizeIdx = -1;
            for (int i = 0; i < Phases.Count; i++)
            {
                if (Phases[i].Id == PhaseCustomize) { customizeIdx = i; break; }
            }
            if (customizeIdx < 0) return 0;

            int end = customizeIdx + 1;
            while (end < Phases.Count && Phases[end].IndentLevel > 0)
                end++;
            return end;
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
                        // Ensure children are visible when parent becomes active
                        SetChildrenVisible(i, true);
                        break;
                    }
                }
            }

            // When activating a parent phase, expand its children
            if (phase.IndentLevel == 0)
                SetChildrenVisible(Phases.IndexOf(phase), true);

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

            // Collapse children when a parent phase completes
            if (phase.IndentLevel == 0)
                SetChildrenVisible(Phases.IndexOf(phase), false);

            _logger.LogDebug("Phase completed: {Phase}", id);
        }

        public void FailPhase(string id, string message)
        {
            var phase = FindPhase(id);
            if (phase == null) return;
            phase.Status = DeploymentPhaseStatus.Failed;
            phase.IsIndeterminate = false;
            phase.ProgressText = message;

            // Collapse children when a parent phase fails
            if (phase.IndentLevel == 0)
                SetChildrenVisible(Phases.IndexOf(phase), false);
        }

        /// <summary>
        /// Shows or hides all IndentLevel>0 children immediately following the parent at <paramref name="parentIndex"/>.
        /// </summary>
        private void SetChildrenVisible(int parentIndex, bool visible)
        {
            if (parentIndex < 0) return;
            for (int i = parentIndex + 1; i < Phases.Count; i++)
            {
                if (Phases[i].IndentLevel <= Phases[parentIndex].IndentLevel)
                    break;
                Phases[i].IsVisible = visible;
            }
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
