using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using VMCreate.MediaHandlers;
using Wpf.Ui.Controls;

namespace VMCreate
{
    /// <summary>
    /// ViewModel for the Deploy page. Pre-builds phase cards from the selected
    /// gallery item and VM settings, then updates them as progress reports arrive.
    /// </summary>
    public class DeployPageViewModel : ViewModelBase
    {
        /// <summary>
        /// Creates a hidden sub-step (IndentLevel=1) that will become visible
        /// automatically when its parent phase is activated, consistent with other phases.
        /// </summary>
        private static DeploymentPhase NewPostBootSubStep(string id, string name, string description, SymbolRegular icon)
            => new DeploymentPhase(id, name, description, icon) { IndentLevel = 1, IsVisible = false };

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

        // Windows-only post-boot sub-step IDs
        public const string SubStabilizeVm     = "Sub_StabilizeVm";
        public const string SubLicenseRearm    = "Sub_LicenseRearm";
        public const string SubRemoveDefender  = "Sub_RemoveDefender";
        public const string SubDisableUpdates  = "Sub_DisableUpdates";
        public const string SubCleanupTasks    = "Sub_CleanupTasks";

        // Linux-only post-boot sub-step IDs
        public const string SubRemoveVBox      = "Sub_RemoveVBox";
        public const string SubSyncTimezone    = "Sub_SyncTimezone";
        public const string SubConfigureVpn    = "Sub_ConfigureVpn";
        public const string SubRestoreSsh      = "Sub_RestoreSsh";

        /// <summary>Generates a sub-card phase ID for a distribution-specific option step.</summary>
        public static string DistOptionSubId(string stepName) => $"Sub_Dist_{stepName}";

        /// <summary>Resolves a string icon name from distribution metadata to a WPF UI SymbolRegular.</summary>
        public static SymbolRegular ResolveIconName(string iconName)
        {
            if (string.IsNullOrWhiteSpace(iconName)) return SymbolRegular.ArrowSync24;
            return Enum.TryParse<SymbolRegular>(iconName, out var icon) ? icon : SymbolRegular.ArrowSync24;
        }

        /// <summary>Looks up a step by name and returns its deployment metadata, if available.</summary>
        private IDistributionOptionMetadata GetStepMetadata(string name)
        {
            if (_configurableSteps != null
                && _configurableSteps.TryGetValue(name, out var step)
                && step is IDistributionOptionMetadata metadata)
            {
                return metadata;
            }
            return null;
        }

        /// <summary>
        /// Returns distribution option steps that are visible for the selected item.
        /// Includes both optional (user-toggled) and required (always-run) steps.
        /// </summary>
        private IEnumerable<IConfigurableCustomizationStep> GetApplicableDistributionSteps(VmCustomizations c)
        {
            if (SelectedItem == null || _configurableSteps == null) return Enumerable.Empty<IConfigurableCustomizationStep>();

            return _configurableSteps.Values
                .Where(s => s.IsVisibleFor(SelectedItem))
                .Where(s =>
                {
                    if (s.IsOptional) return c?.DistributionOptions?.Any(o => string.Equals(o.Name, s.Name, StringComparison.OrdinalIgnoreCase) && o.IsEnabled) == true;
                    return true;
                })
                .OrderBy(s => (s as IDistributionOptionMetadata)?.DeployOrder ?? int.MaxValue)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Returns enabled distribution options sorted by their deployment order.</summary>
        private IEnumerable<DistributionOptionSelection> GetEnabledDistributionOptions(VmCustomizations c)
        {
            if (c?.DistributionOptions == null) return Enumerable.Empty<DistributionOptionSelection>();
            return c.DistributionOptions
                .Where(o => o?.IsEnabled == true)
                .OrderBy(o => GetStepMetadata(o.Name)?.DeployOrder ?? o.Order)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase);
        }

        private readonly ILogger _logger;
        private readonly IReadOnlyDictionary<string, IConfigurableCustomizationStep> _configurableSteps;
        private bool _isDeploying;
        private bool _isComplete;
        private bool _hasFailed;
        private string _vmName;
        private string _errorMessage;

        public event Action<WizardResult> RequestWizardComplete;
        public event Action RequestCancel;

        public DeployPageViewModel(WizardData wizardData, ILogger logger, IEnumerable<IConfigurableCustomizationStep> configurableSteps = null)
        {
            if (wizardData == null) throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configurableSteps = (configurableSteps ?? Enumerable.Empty<IConfigurableCustomizationStep>())
                .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

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

        /// <summary>
        /// True when the selected image is a native Hyper-V Windows VM that requires
        /// an elevated unattend injection (UAC prompt) before first boot.
        /// </summary>
        public bool ShowUnattendInjectionInfo =>
            SelectedItem?.IsNativeHyperV == true
            && SelectedItem?.IsWindows == true;

        public ICommand CancelCommand { get; }
        public ICommand ConnectToVmCommand { get; }
        public ICommand OpenHyperVManagerCommand { get; }
        public ICommand NewVmCommand { get; }

        // ── Phase list construction ──────────────────────────────────────

        private void BuildPhaseList(WizardData wizardData)
        {
            // Use GalleryItem.FileType, not DiskFileDetector, because the gallery item
            // peels off compression wrappers (e.g. .vmdk.xz -> VMDK) so the UI can show
            // the correct phases before extraction reveals the real disk file.
            DiskImageFormat format = DiskImageFormatExtensions.FromExtension(wizardData.SelectedItem?.FileType);
            bool isNativeHyperV = wizardData.SelectedItem?.IsNativeHyperV == true;
            bool isIso = format == DiskImageFormat.Iso;
            bool needsExtraction = wizardData.SelectedItem?.NeedsExtraction ?? false;
            bool needsConversion = !isNativeHyperV
                && format is DiskImageFormat.Vmdk or DiskImageFormat.Qcow2 or DiskImageFormat.Ova or DiskImageFormat.Archive;
            bool nestedVirt = wizardData.Settings?.VirtualizationEnabled ?? true;

            if (ShowUnattendInjectionInfo)
            {
                Phases.Add(new DeploymentPhase("Sub_UnattendInfo", "Administrator approval required",
                    "This Windows image needs to be modified before first boot (to create the user account and enable remote access). You will be prompted for administrator approval at deployment time.",
                    SymbolRegular.Info24)
                { Status = DeploymentPhaseStatus.Information });
            }

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

            // Pre-boot and post-boot customizations only apply to disk-image flows
            // (VMDK, QCOW2, VHDX, etc.). ISO installer images and native Hyper-V
            // images have no customization pipeline — the user installs interactively
            // or the image is pre-built.
            if (!isNativeHyperV && !isIso)
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
            else if (wizardData.SelectedItem?.IsWindows == true)
            {
                // Windows images are configured post-boot via PowerShell Direct.
                // Only the distribution option steps apply — no Linux infra steps.
                var c = wizardData.Customizations;
                bool hasWindowsPostBoot = GetApplicableDistributionSteps(c).Any();
                if (hasWindowsPostBoot)
                {
                    Phases.Add(new DeploymentPhase(PhasePostBoot, "Post-Boot Config",
                        "Configuring the VM via PowerShell Direct",
                        SymbolRegular.Settings24));
                    AddWindowsPostBootSubSteps(c);
                }
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
            bool isIso = DiskFileDetector.DetectFileType(wizardData.SelectedItem?.DiskUri) == DiskImageFormat.Iso;

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
            Phases.Add(NewPostBootSubStep(SubAddTempNic, "Add Temporary NIC",
                "Adding temporary network adapter for SSH access", SymbolRegular.PlugConnected24));
            Phases.Add(NewPostBootSubStep(SubWaitForSsh, "Waiting for VM",
                "Waiting for the VM to accept remote management connections", SymbolRegular.PlugConnected24));
            Phases.Add(NewPostBootSubStep(SubRemoveVBox, "Remove VBox Guest Additions",
                "Cleaning up VirtualBox artifacts", SymbolRegular.Delete24));
            if (c?.SyncTimezone == true)
            {
                Phases.Add(NewPostBootSubStep(SubSyncTimezone, "Sync Timezone",
                    "Setting guest timezone to match host", SymbolRegular.Clock24));
            }
            if (c?.ConfigureHtbVpn == true)
            {
                Phases.Add(NewPostBootSubStep(SubConfigureVpn, "Configure VPN",
                    "Installing OpenVPN and deploying VPN configs", SymbolRegular.Globe24));
            }
            AddDistributionOptionSubSteps(c);
            Phases.Add(NewPostBootSubStep(SubRestoreSsh, "Restore SSH State",
                "Restoring the original SSH configuration", SymbolRegular.ShieldKeyhole24));
        }

        /// <summary>Appends Windows post-boot sub-step cards (IndentLevel=1, hidden): a connect
        /// step plus one per enabled distribution option. No Linux infra steps (NIC/SSH/VBox).</summary>
        private void AddWindowsPostBootSubSteps(VmCustomizations c)
        {
            Phases.Add(NewPostBootSubStep(SubWaitForSsh, "Waiting for VM",
                "Waiting for the VM to accept remote management connections", SymbolRegular.PlugConnected24));
            AddDistributionOptionSubSteps(c);
            AddCompletionInfoCards(c);
        }

        /// <summary>Adds visible top-level completion/info cards (IndentLevel=0) declared by enabled optional steps.</summary>
        private void AddCompletionInfoCards(VmCustomizations c)
        {
            foreach (var step in GetApplicableDistributionSteps(c).Where(s => s.IsOptional))
            {
                var metadata = step as IDistributionOptionMetadata;
                if (string.IsNullOrWhiteSpace(metadata?.DeployCompletionInfo))
                    continue;

                string id = (metadata.DeployPhaseId ?? DistOptionSubId(step.Name)) + "_Info";
                if (Phases.Any(p => p.Id == id)) continue;
                Phases.Add(new DeploymentPhase(id, $"{metadata.DeployTitle} Background Setup",
                    metadata.DeployCompletionInfo, SymbolRegular.Info24)
                {
                    IndentLevel = 0,
                    Status = DeploymentPhaseStatus.Information
                });
            }
        }

        /// <summary>Inserts post-boot sub-step cards at a given index (hidden). Returns the next free index.</summary>
        private int InsertPostBootSubStepsAt(int index, VmCustomizations c)
        {
            Phases.Insert(index++, NewPostBootSubStep(SubAddTempNic, "Add Temporary NIC",
                "Adding temporary network adapter for SSH access", SymbolRegular.PlugConnected24));
            Phases.Insert(index++, NewPostBootSubStep(SubWaitForSsh, "Waiting for VM",
                "Waiting for the VM to accept remote management connections", SymbolRegular.PlugConnected24));
            Phases.Insert(index++, NewPostBootSubStep(SubRemoveVBox, "Remove VBox Guest Additions",
                "Cleaning up VirtualBox artifacts", SymbolRegular.Delete24));
            if (c?.SyncTimezone == true)
            {
                Phases.Insert(index++, NewPostBootSubStep(SubSyncTimezone, "Sync Timezone",
                    "Setting guest timezone to match host", SymbolRegular.Clock24));
            }
            if (c?.ConfigureHtbVpn == true)
            {
                Phases.Insert(index++, NewPostBootSubStep(SubConfigureVpn, "Configure VPN",
                    "Installing OpenVPN and deploying VPN configs", SymbolRegular.Globe24));
            }
            index = InsertDistributionOptionSubStepsAt(index, c);
            Phases.Insert(index++, NewPostBootSubStep(SubRestoreSsh, "Restore SSH State",
                "Restoring the original SSH configuration", SymbolRegular.ShieldKeyhole24));
            return index;
        }

        /// <summary>Appends distribution-option sub-steps in deployment order from step metadata.</summary>
        private void AddDistributionOptionSubSteps(VmCustomizations c)
        {
            foreach (var step in GetApplicableDistributionSteps(c))
            {
                var metadata = step as IDistributionOptionMetadata;
                string id = metadata?.DeployPhaseId ?? DistOptionSubId(step.Name);
                string title = metadata?.DeployTitle ?? step.Name;
                string description = metadata?.DeployDescription ?? "Running distribution-specific tool";
                SymbolRegular icon = ResolveIconName(metadata?.DeployIconName);

                if (Phases.Any(p => p.Id == id)) continue;
                Phases.Add(NewPostBootSubStep(id, title, description, icon));
            }
        }

        /// <summary>Inserts distribution-option sub-steps at a given index. Returns the next free index.</summary>
        private int InsertDistributionOptionSubStepsAt(int index, VmCustomizations c)
        {
            foreach (var step in GetApplicableDistributionSteps(c))
            {
                var metadata = step as IDistributionOptionMetadata;
                string id = metadata?.DeployPhaseId ?? DistOptionSubId(step.Name);
                string title = metadata?.DeployTitle ?? step.Name;
                string description = metadata?.DeployDescription ?? "Running distribution-specific tool";
                SymbolRegular icon = ResolveIconName(metadata?.DeployIconName);

                if (Phases.Any(p => p.Id == id)) continue;
                Phases.Insert(index++, NewPostBootSubStep(id, title, description, icon));
            }
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
