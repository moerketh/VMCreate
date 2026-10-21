using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using VMCreate.HyperV.VmCreation;
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

        // Post-boot infrastructure sub-step IDs
        public const string SubAddTempNic   = "Sub_AddTempNic";
        public const string SubWaitForSsh   = "Sub_WaitForSsh";
        public const string SubWaitForKvpHeartbeat = "Sub_WaitForKvpHeartbeat";

        // Windows-only post-boot sub-step IDs
        public const string SubStabilizeVm     = "Sub_StabilizeVm";
        public const string SubLicenseRearm    = "Sub_LicenseRearm";
        public const string SubRemoveDefender  = "Sub_RemoveDefender";
        public const string SubDisableUpdates  = "Sub_DisableUpdates";
        public const string SubCleanupTasks    = "Sub_CleanupTasks";

        // Linux-only post-boot sub-step IDs
        public const string SubRemoveVBox      = "Sub_RemoveVBox";
        public const string SubSyncTimezone    = "Sub_SyncTimezone";
        public const string SubInstallOpenVpn  = "Sub_InstallOpenVpn";
        public const string SubConfigureVpn    = "Sub_ConfigureVpn";
        public const string SubRestoreSsh      = "Sub_RestoreSsh";

        // Lamco (Wayland-native RDP) post-boot sub-step IDs — matched by
        // InstallLamcoRdpStep/EnableGraphicalAutologinStep ProgressPhaseId.
        // SubStartGraphicalSession is matched by StartGraphicalSessionStep
        // (order 239 — activates the session without a manual reboot).
        public const string SubInstallLamcoRdp  = "Sub_InstallLamcoRdp";
        public const string SubEnableAutologin  = "Sub_EnableAutologin";
        public const string SubStartGraphicalSession = "Sub_StartGraphicalSession";

        // wayrdp (MIT FreeRDP-3 Wayland-native RDP) post-boot
        // sub-step ID — matched by InstallWayrdpRdpStep.ProgressPhaseId.
        // The session cards (autologin/session start) are shared with Lamco.
        public const string SubInstallWayrdpRdp = "Sub_InstallWayrdpRdp";

        // Auto RDP backend: detection + the possible backfilled installs.
        // Sub_AutoRdpResolve is matched by AutoRdpBackendResolveStep.
        // ProgressPhaseId; the branch cards are reconciled at runtime by
        // ReconcilePostBootSteps once the resolver picks a backend.
        public const string SubAutoRdpResolve      = "Sub_AutoRdpResolve";
        public const string SubInstallXrdpPostBoot = "Sub_InstallXrdpPostBoot";

        /// <summary>Resolves a string icon name from distribution metadata to a WPF UI SymbolRegular.</summary>
        public static SymbolRegular ResolveIconName(string? iconName)
        {
            if (string.IsNullOrWhiteSpace(iconName)) return SymbolRegular.ArrowSync24;
            return Enum.TryParse<SymbolRegular>(iconName, out var icon) ? icon : SymbolRegular.ArrowSync24;
        }

        /// <summary>
        /// Returns distribution option steps that are visible for the selected item.
        /// Includes both optional (user-toggled) and required (always-run) steps.
        /// Applicability is honored for both kinds: an optional step needs an
        /// enabled selection, and a required step (e.g. DesktopSwitchStep, or
        /// the Flare-VM steps on Windows images) needs
        /// <see cref="IStepVisibility"/>-style applicability against the
        /// actual customizations — required does not mean unconditional.
        /// </summary>
        private IEnumerable<IConfigurableCustomizationStep> GetApplicableDistributionSteps(VmCustomizations? c)
        {
            if (SelectedItem == null || _configurableSteps == null) return Enumerable.Empty<IConfigurableCustomizationStep>();

            return _configurableSteps.Values
                .Where(s => s.IsVisibleFor(SelectedItem))
                .Where(s =>
                {
                    if (s.IsOptional) return c?.DistributionOptions?.Any(o => string.Equals(o.Name, s.Name, StringComparison.OrdinalIgnoreCase) && o.IsEnabled) == true;
                    return s.IsApplicable(SelectedItem, c);
                })
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
        }

        private readonly ILogger _logger;
        private readonly IReadOnlyDictionary<string, IConfigurableCustomizationStep>? _configurableSteps;
        private readonly IPostBootStepPlanner _planner;
        private bool _isDeploying;
        private bool _isComplete;
        private bool _hasFailed;
        private string _vmName = "";
        private string _errorMessage = "";

        public event Action<WizardResult>? RequestWizardComplete;
        public event Action? RequestCancel;

        /// <summary>Raised when the user asks to retry the failed deployment.</summary>
        public event Action? RequestRetry;

        /// <summary>Raised when the user asks to go back and change settings.</summary>
        public event Action? RequestNavigateBack;

        public DeployPageViewModel(
            WizardData wizardData,
            ILogger logger,
            IEnumerable<IConfigurableCustomizationStep>? configurableSteps = null,
            IPostBootStepPlanner? planner = null)
        {
            if (wizardData == null) throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configurableSteps = (configurableSteps ?? Enumerable.Empty<IConfigurableCustomizationStep>())
                .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            _planner = planner ?? new PostBootStepPlanner(Enumerable.Empty<ICustomizationStep>());

            VmName = wizardData.Settings?.VMName ?? "VM";
            SelectedItem = wizardData.SelectedItem;
            _lastCustomizations = wizardData.Customizations;
            _lastSettings = wizardData.Settings;
            CancelCommand = new RelayCommand(OnCancel, () => _isDeploying && !_isComplete);
            ConnectToVmCommand = new RelayCommand(OnConnectToVm);
            OpenHyperVManagerCommand = new RelayCommand(OnOpenHyperVManager);
            NewVmCommand = new RelayCommand(() => RequestWizardComplete?.Invoke(WizardResult.Finished));
            RetryCommand = new RelayCommand(() => RequestRetry?.Invoke());
            BackCommand = new RelayCommand(() => RequestNavigateBack?.Invoke());
            OpenLogCommand = new RelayCommand(OpenLog);

            BuildSummaryRows(wizardData);
            BuildPhaseList(wizardData);
        }

        public ObservableCollection<DeploymentPhase> Phases { get; } = new ObservableCollection<DeploymentPhase>();

        public GalleryItem? SelectedItem { get; }

        /// <summary>
        /// Version suffix for the deploy header (e.g. "2026-W26" for a
        /// non-default release variant). Empty when the item reports no
        /// meaningful version.
        /// </summary>
        public string SelectedItemVersionText
        {
            get
            {
                var version = SelectedItem?.Version;
                return string.IsNullOrEmpty(version) || version == "Unknown"
                    ? ""
                    : $" · {version}";
            }
        }

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
        public ICommand RetryCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand OpenLogCommand { get; }

        /// <summary>Summary rows (label/value) describing the deployment, shown on the header card.</summary>
        public ObservableCollection<DeploymentSummaryRow> SummaryRows { get; } = new ObservableCollection<DeploymentSummaryRow>();

        // ── Summary rows ────────────────────────────────────────────────

        private void BuildSummaryRows(WizardData wizardData)
        {
            var s = wizardData.Settings;
            if (s == null) return;

            SummaryRows.Add(new DeploymentSummaryRow("Memory",
                s.MemoryInMB >= 1024 ? $"{s.MemoryInMB / 1024.0:0.#} GB" : $"{s.MemoryInMB} MB"));
            SummaryRows.Add(new DeploymentSummaryRow("CPU",
                s.CPUCount == 1 ? "1 core" : $"{s.CPUCount} cores"));

            if (s.AutoDetectDiskSize)
                SummaryRows.Add(new DeploymentSummaryRow("Disk", "Auto-detect"));
            else
                SummaryRows.Add(new DeploymentSummaryRow("Disk", $"{s.NewDriveSizeInGB} GB"));

            if (s.VirtualizationEnabled)
                SummaryRows.Add(new DeploymentSummaryRow("Nested virtualization", "Enabled"));

            var c = wizardData.Customizations;
            SummaryRows.Add(new DeploymentSummaryRow("Integration services",
                c.EnableIntegrationServices ? "Enabled" : "Disabled"));

            var fileType = wizardData.SelectedItem?.FileType;
            var format = DiskImageFormatExtensions.FromExtension(fileType);
            bool iso = format == DiskImageFormat.Iso;
            bool nativeHyperV = wizardData.SelectedItem?.IsNativeHyperV == true;
            if (!iso && !nativeHyperV && wizardData.SelectedItem?.IsWindows == false)
            {
                SummaryRows.Add(new DeploymentSummaryRow("Remote desktop", c.RdpBackend switch
                {
                    RdpBackend.Auto => "Auto",
                    RdpBackend.Xrdp => "xrdp",
                    RdpBackend.Lamco => "Lamco RDP",
                    _ => "None"
                }));
                SummaryRows.Add(new DeploymentSummaryRow("DNS",
                    c.DnsMode == DnsMode.Custom ? "Custom" : "Host"));
            }
        }

        private void OpenLog()
        {
            try
            {
                Process.Start(new ProcessStartInfo(App.LogFilePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open log file {Path}", App.LogFilePath);
            }
        }

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
                // The cloud-init flow never boots the cloning ISO, so it has
                // no pre-boot stage at all — chroot-capable steps run
                // post-boot there (fallback) and xrdp is not a cloud-init
                // concern.
                bool isCloudInitFlow = wizardData.SelectedItem?.HasTag("cloud-init") == true;

                // Show pre-boot customization card upfront if any pre-boot
                // work exists: explicit xrdp OR any chroot-capable step
                // (they execute inside the ISO stage on this flow).
                bool hasPreBoot = !isCloudInitFlow
                    && (wizardData.Customizations?.HasPreBootCustomizations == true
                        || _planner.HasChrootSteps(SelectedItem, wizardData.Customizations));
                if (hasPreBoot)
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
                _lastPostBootFlow = wizardData.SelectedItem?.HasTag("cloud-init") == true
                    ? PostBootFlow.DiskImageCloudInit
                    : PostBootFlow.DiskImageIsoCycle;
                InsertPostBootSubStepsAt(
                    Phases.Count,
                    wizardData.Customizations,
                    _lastPostBootFlow);
            }
            else if (wizardData.SelectedItem?.IsWindows == true)
            {
                // Windows images are configured post-boot via PowerShell Direct.
                // Only the distribution option steps apply — no Linux infra steps.
                var c = wizardData.Customizations;
                bool hasWindowsPostBoot = _planner.GetPlannedSteps(StepPlatform.Windows, wizardData.SelectedItem, c).Any();
                if (hasWindowsPostBoot)
                {
                    Phases.Add(new DeploymentPhase(PhasePostBoot, "Post-Boot Config",
                        "Configuring the VM via PowerShell Direct",
                        SymbolRegular.Settings24));
                    InsertPostBootSubStepsAt(Phases.Count, c, PostBootFlow.Direct);
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
            InsertPostBootSubStepsAt(doneIndex + 1, _lastCustomizations, PostBootFlow.Direct);
        }

        /// <summary>
        /// Recomputes BOTH dynamic card regions — the Customize (chroot) list
        /// and the PostBoot list — against the planner's current plan.
        /// Called on every step report (chroot and post-boot) because step
        /// applicability changes mid-run: <c>AutoRdpBackendResolveStep</c>
        /// (order 232, chroot-capable) rewrites
        /// <see cref="VmCustomizations.RdpBackend"/> in place, which makes
        /// the chosen branch's steps newly applicable and the losing
        /// branch's steps newly inapplicable — in whichever region they
        /// live. Newly applicable steps get a card inserted at their Order
        /// position; pending cards whose step is no longer applicable are
        /// removed. Completed and active cards are never touched — they
        /// already ran and their status is history, not plan.
        /// </summary>
        public void ReconcilePostBootSteps()
        {
            if (_lastCustomizations == null || SelectedItem == null) return;

            ReconcileRegion(PhaseCustomize, _planner.GetPlannedChrootSteps(SelectedItem, _lastCustomizations), allowGateSplit: false);
            ReconcileRegion(PhasePostBoot, GetPlannedPostBootSteps(
                SelectedItem.IsWindows ? StepPlatform.Windows : StepPlatform.Linux,
                SelectedItem, _lastCustomizations, _lastPostBootFlow), allowGateSplit: true);
        }

        /// <summary>
        /// One region pass: [parent] then its contiguous indent-1 cards.
        /// Newly applicable planned steps get a card at their plan position
        /// (split around the KVP gate card when the region has one);
        /// pending cards not in the plan are removed; completed/active cards
        /// and infrastructure cards are never touched.
        /// </summary>
        private void ReconcileRegion(string parentId, IReadOnlyList<ICustomizationStep> planned, bool allowGateSplit)
        {
            int parentIndex = -1;
            for (int i = 0; i < Phases.Count; i++)
            {
                if (Phases[i].Id == parentId) { parentIndex = i; break; }
            }
            if (parentIndex < 0) return;

            // The contiguous indent-1 run directly following the parent
            // (completion-info cards have IndentLevel 0 and live after it).
            int listStart = parentIndex + 1;
            int listEnd = listStart;
            while (listEnd < Phases.Count && Phases[listEnd].IndentLevel > 0)
                listEnd++;

            string[] plannedIds = planned.Select(PostBootStepPlanner.CardId).ToArray();

            // Insert any newly applicable step's card at its position among the
            // planner-ordered cards. The existing list is already in plan order,
            // so each new card goes directly before the first card whose planned
            // position is later than its own; when no such card exists it goes
            // at the end of its half of the region. The KVP gate card splits the
            // region exactly like the builder: a step below SshRestoreOrder
            // belongs before it, a step at or above it belongs after it — the
            // anchor search must never cross the gate. (The Customize region
            // has no gate card — allowGateSplit is false there.)
            int kvpIndex = -1;
            if (allowGateSplit)
            {
                for (int i = listStart; i < listEnd; i++)
                {
                    if (Phases[i].Id == SubWaitForKvpHeartbeat) { kvpIndex = i; break; }
                }
            }

            for (int p = 0; p < planned.Count; p++)
            {
                string id = plannedIds[p];
                bool exists = false;
                for (int i = listStart; i < listEnd; i++)
                {
                    if (string.Equals(Phases[i].Id, id, StringComparison.Ordinal)) { exists = true; break; }
                }
                if (exists) continue;

                bool inTail = planned[p].Order >= PostBootStepPlanner.SshRestoreOrder;
                int searchStart = listStart;
                int searchEnd = listEnd;
                if (kvpIndex >= 0)
                {
                    if (inTail) searchStart = kvpIndex + 1;
                    else searchEnd = kvpIndex;   // never anchor at/after the gate
                }

                int insertAt = searchEnd;
                for (int i = searchStart; i < searchEnd; i++)
                {
                    if (IndexOfStepId(Phases[i].Id, plannedIds) > p)
                    {
                        insertAt = i;
                        break;
                    }
                }

                var step = planned[p];
                Phases.Insert(insertAt, BuildCardForStep(step));
                listEnd++;
                if (insertAt <= kvpIndex) kvpIndex++;
            }

            // Remove pending cards whose step is no longer applicable. Iterate
            // backwards so removals don't shift the pending range.
            for (int i = listEnd - 1; i >= listStart; i--)
            {
                string id = Phases[i].Id;
                int pos = IndexOfStepId(id, plannedIds);
                if (pos >= 0) continue;
                if (Phases[i].Status != DeploymentPhaseStatus.Pending) continue;
                // Infrastructure cards (temp NIC, SSH wait, KVP gate) are not
                // planner cards; keep them.
                if (IsInfrastructureCard(id)) continue;
                Phases.RemoveAt(i);
                listEnd--;
            }
        }

        private static int IndexOfStepId(string id, string[] plannedIds)
        {
            for (int i = 0; i < plannedIds.Length; i++)
            {
                if (string.Equals(plannedIds[i], id, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        /// <summary>
        /// Cards that are NOT planner step cards: infrastructure prelude/gate
        /// cards in the PostBoot region and the scenario scripts' built-in
        /// sub-steps in the Customize region. Reconcile keeps them — they
        /// represent work the creation strategy/scenario runs regardless of
        /// the planned step list.
        /// </summary>
        private static bool IsInfrastructureCard(string id)
            => id is SubAddTempNic or SubWaitForSsh or SubWaitForKvpHeartbeat
               or SubInstallHyperV or SubInstallXrdp or SubSshSetup or SubReboot
               or SubInstallGrub or PhaseCloneDisk;

        /// <summary>Creates the post-boot card for a planned step, from its display info.</summary>
        private static DeploymentPhase BuildCardForStep(ICustomizationStep step)
        {
            string id = PostBootStepPlanner.CardId(step);
            var display = step as IStepDisplayInfo;
            string title = !string.IsNullOrWhiteSpace(display?.DeployTitle) ? display.DeployTitle : step.Name;
            string description = !string.IsNullOrWhiteSpace(display?.DeployDescription)
                ? display.DeployDescription
                : "Running " + step.Name;
            SymbolRegular icon = ResolveIconName(display?.DeployIconName);
            return NewPostBootSubStep(id, title, description, icon);
        }

        /// <summary>Builds a descriptive string for the post-boot card based on what's enabled.</summary>
        private static string BuildPostBootDescription(VmCustomizations c)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (c.SyncTimezone) parts.Add("timezone sync");
            if (c.InstallOpenVpn) parts.Add("OpenVPN install");
            if (c.ConfigureHtbVpn) parts.Add("HTB VPN deployment");
            return parts.Count > 0
                ? "SSH into VM to apply: " + string.Join(", ", parts)
                : "Applying post-boot customizations via SSH";
        }

        // ── Sub-step helpers ─────────────────────────────────────────────

        /// <summary>Stashed customizations so dynamic Insert* methods can add the right sub-steps.</summary>
        private VmCustomizations? _lastCustomizations;

        /// <summary>Stashed settings so dynamic Insert* methods can add conditional sub-steps.</summary>
        private VmSettings? _lastSettings;

        /// <summary>
        /// The post-boot flow the phase list was built for, so runtime
        /// re-planning (ReconcilePostBootSteps) keeps excluding chroot steps
        /// the same way the initial build did.
        /// </summary>
        private PostBootFlow _lastPostBootFlow = PostBootFlow.Direct;

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

        /// <summary>Appends pre-boot sub-step cards (IndentLevel=1, hidden) to the end of Phases.</summary>
        private void AddPreBootSubSteps(VmCustomizations? c)
        {
            Phases.Add(new DeploymentPhase(SubInstallHyperV, "Install Hyper-V packages",
                "Installing guest integration services", SymbolRegular.Box24) { IndentLevel = 1, IsVisible = false });
            if (c?.ConfigureXrdp == true)
            {
                Phases.Add(new DeploymentPhase(SubInstallXrdp, "Install xRDP",
                    "Installing xRDP for Enhanced Session support", SymbolRegular.Desktop24) { IndentLevel = 1, IsVisible = false });
            }
            InsertChrootStepCards(Phases.Count, c);
            Phases.Add(new DeploymentPhase(SubSshSetup, "SSH setup",
                "Creating automation user and injecting SSH key", SymbolRegular.Key24) { IndentLevel = 1, IsVisible = false });
            Phases.Add(new DeploymentPhase(SubReboot, "Reboot",
                "Shutting down VM to boot from converted disk", SymbolRegular.Power24) { IndentLevel = 1, IsVisible = false });
        }

        /// <summary>Inserts pre-boot sub-step cards at a given index (hidden). Returns the next free index.</summary>
        private int InsertPreBootSubStepsAt(int index, VmCustomizations? c)
        {
            Phases.Insert(index++, new DeploymentPhase(SubInstallHyperV, "Install Hyper-V packages",
                "Installing guest integration services", SymbolRegular.Box24) { IndentLevel = 1, IsVisible = false });
            if (c?.ConfigureXrdp == true)
            {
                Phases.Insert(index++, new DeploymentPhase(SubInstallXrdp, "Install xRDP",
                    "Installing xRDP for Enhanced Session support", SymbolRegular.Desktop24) { IndentLevel = 1, IsVisible = false });
            }
            index = InsertChrootStepCards(index, c);
            Phases.Insert(index++, new DeploymentPhase(SubSshSetup, "SSH setup",
                "Creating automation user and injecting SSH key", SymbolRegular.Key24) { IndentLevel = 1, IsVisible = false });
            Phases.Insert(index++, new DeploymentPhase(SubReboot, "Reboot",
                "Shutting down VM to boot from converted disk", SymbolRegular.Power24) { IndentLevel = 1, IsVisible = false });
            return index;
        }

        /// <summary>
        /// Inserts one card per chroot-capable planned step — the same list,
        /// order and card IDs the chroot executor reports with
        /// (Phase=Customize, StepName=step.Name → CardId), so each report
        /// activates its card. Steps whose card already exists are skipped
        /// (idempotent for the Insert*/Add* call pairs).
        /// </summary>
        private int InsertChrootStepCards(int index, VmCustomizations? c)
        {
            // Chroot-ONLY steps get their card in the Customize phase;
            // verify-semantics steps (RunsAfterChroot) run both halves, so
            // their card stays with the post-boot list (the live verify is
            // the user-visible completion).
            foreach (var step in _planner.GetChrootOnlySteps(SelectedItem, c))
            {
                string id = PostBootStepPlanner.CardId(step);
                if (Phases.Any(p => p.Id == id)) continue;
                Phases.Insert(index++, BuildCardForStep(step));
            }
            return index;
        }

        /// <summary>
        /// Builds the post-boot sub-step cards (IndentLevel=1, hidden) in the
        /// exact order the backend runs them: infrastructure prelude, planned
        /// steps below the SSH-restore boundary, the KVP heartbeat gate (when
        /// enabled), then the SSH-restore tail. The step cards come from
        /// <see cref="IPostBootStepPlanner"/> — the same ordering the backend
        /// executes with — so the card list can never drift from execution.
        /// </summary>
        /// <param name="index">Index in <see cref="Phases"/> to insert at; returns the next free index.</param>
        /// <param name="c">Customizations the plan is computed against.</param>
        /// <param name="flow">Which infrastructure prelude and gates the creation strategy performs.</param>
        private int InsertPostBootSubStepsAt(int index, VmCustomizations? c, PostBootFlow flow)
        {
            StepPlatform platform = SelectedItem?.IsWindows == true
                ? StepPlatform.Windows
                : StepPlatform.Linux;

            // Infrastructure prelude — mirrors what each creation strategy reports.
            if (flow == PostBootFlow.DiskImageIsoCycle)
            {
                Phases.Insert(index++, NewPostBootSubStep(SubAddTempNic, "Add Temporary NIC",
                    "Adding temporary network adapter for SSH access", SymbolRegular.PlugConnected24));
            }
            Phases.Insert(index++, NewPostBootSubStep(SubWaitForSsh, "Waiting for VM",
                "Waiting for the VM to accept remote management connections", SymbolRegular.PlugConnected24));

            var planned = GetPlannedPostBootSteps(platform, SelectedItem, c, flow);

            // Steps before the KVP gate: everything below the SSH-restore boundary.
            foreach (var step in planned.Where(s => s.Order < PostBootStepPlanner.SshRestoreOrder))
                Phases.Insert(index++, BuildCardForStep(step));

            // The KVP heartbeat gate: the disk-image strategy reports it between
            // the two post-boot halves, when integration services are enabled.
            if (platform == StepPlatform.Linux
                && flow is PostBootFlow.DiskImageIsoCycle or PostBootFlow.DiskImageCloudInit
                && c?.EnableIntegrationServices == true)
            {
                Phases.Insert(index++, NewPostBootSubStep(SubWaitForKvpHeartbeat, "Wait for KVP Heartbeat",
                    "Waiting for the Hyper-V key-value exchange to come up", SymbolRegular.Heart24));
            }

            // The SSH-restore tail (Order >= 900).
            foreach (var step in planned.Where(s => s.Order >= PostBootStepPlanner.SshRestoreOrder))
                Phases.Insert(index++, BuildCardForStep(step));

            // Visible completion-info cards declared by enabled optional steps.
            index = InsertCompletionInfoCardsAt(index, c);

            return index;
        }

        /// <summary>
        /// The post-boot steps that will actually RUN post-boot on this
        /// deployment. On the ISO-cycle flow, chroot-capable steps execute
        /// inside the ISO stage instead (their cards live in the Customize
        /// phase; the backend's run ledger skips them post-boot), so they are
        /// excluded here. The cloud-init flow has no ISO stage — the same
        /// steps run post-boot there.
        /// </summary>
        private IReadOnlyList<ICustomizationStep> GetPlannedPostBootSteps(
            StepPlatform platform, GalleryItem? item, VmCustomizations? c, PostBootFlow flow)
        {
            var planned = _planner.GetPlannedSteps(platform, item, c);
            if (flow != PostBootFlow.DiskImageIsoCycle || platform != StepPlatform.Linux)
                return planned;

            // Chroot-ONLY steps run in the ISO stage and are skipped
            // post-boot by the ledger; verify-semantics steps
            // (RunsAfterChroot) run in BOTH halves and keep their post-boot
            // card for the live verification pass.
            var chrootOnlyNames = new HashSet<string>(
                _planner.GetChrootOnlySteps(item, c).Select(s => s.Name),
                StringComparer.Ordinal);
            return planned.Where(s => !chrootOnlyNames.Contains(s.Name)).ToList();
        }

        /// <summary>
        /// Which infrastructure prelude and gates the deployment's creation strategy performs.
        /// </summary>
        private enum PostBootFlow
        {
            /// <summary>
            /// The disk-image ISO-cycle flow (<c>DiskImageVmCreationStrategy</c>,
            /// non-cloud-init): adds a temporary NIC for SSH, waits for SSH, runs
            /// steps, waits for the KVP heartbeat, runs the restore tail.
            /// </summary>
            DiskImageIsoCycle,

            /// <summary>
            /// The disk-image cloud-init flow (same strategy, items tagged
            /// "cloud-init"): no temp NIC — cloud-init provisions the SSH user —
            /// but the KVP heartbeat gate still runs.
            /// </summary>
            DiskImageCloudInit,

            /// <summary>
            /// Every other flow (ISO installer, native Hyper-V Windows): only the
            /// SSH/PowerShell-Direct wait plus the steps — no temp NIC, no KVP gate.
            /// </summary>
            Direct,
        }

        /// <summary>Adds visible top-level completion/info cards (IndentLevel=0) declared by enabled optional steps.</summary>
        private int InsertCompletionInfoCardsAt(int index, VmCustomizations? c)
        {
            foreach (var step in GetApplicableDistributionSteps(c).Where(s => s.IsOptional))
            {
                if (step is not IDistributionOptionMetadata metadata) continue;
                if (string.IsNullOrWhiteSpace(metadata.DeployCompletionInfo))
                    continue;

                // Derived from the step's own card ID (CardId) so the info card
                // stays in lockstep with its step card's identity.
                string id = PostBootStepPlanner.CardId(step) + "_Info";
                if (Phases.Any(p => p.Id == id)) continue;
                Phases.Insert(index++, new DeploymentPhase(id, $"{metadata.DeployTitle} Background Setup",
                    metadata.DeployCompletionInfo, SymbolRegular.Info24)
                {
                    IndentLevel = 0,
                    Status = DeploymentPhaseStatus.Information
                });
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

        public DeploymentPhase? FindPhase(string id) =>
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

        public void UpdatePhaseProgress(string id, int percentage, string? text = null)
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

    /// <summary>A simple label/value pair shown on the Deploy page summary card.</summary>
    public sealed class DeploymentSummaryRow
    {
        public DeploymentSummaryRow(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public string Value { get; }
    }
}
