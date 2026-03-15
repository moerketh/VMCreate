using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace VMCreate
{
    public partial class DeployPage : Page
    {
        public event EventHandler<WizardResultEventArgs> WizardCompleted;

        private readonly DeployPageViewModel _viewModel;
        private readonly WizardData _wizardData;
        private readonly CreateVM _createVM;
        private readonly ILogger _logger;
        private CancellationTokenSource _cts;
        private string _activePhaseId;
        private string _activeSubStepId;

        public DeployPage(WizardData wizardData, CreateVM createVM, ILoggerFactory loggerFactory)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _createVM = createVM ?? throw new ArgumentNullException(nameof(createVM));
            if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));

            _logger = loggerFactory.CreateLogger<DeployPage>();
            _viewModel = new DeployPageViewModel(wizardData, _logger);

            InitializeComponent();
            DataContext = _viewModel;

            _viewModel.RequestCancel += OnCancelRequested;
            _viewModel.RequestWizardComplete += result =>
                WizardCompleted?.Invoke(this, new WizardResultEventArgs(result));

            Loaded += async (_, __) => await StartDeploymentAsync();
            Unloaded += (_, __) => Cleanup();
        }

        /// <summary>
        /// Cancels any running deployment and disposes resources.
        /// Called when the page is unloaded (e.g. user navigated away).
        /// </summary>
        private void Cleanup()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _logger.LogInformation("DeployPage unloaded — cancelling in-progress deployment.");
                _cts.Cancel();
            }
            _cts?.Dispose();
            _cts = null;
        }

        private void OnCancelRequested()
        {
            _cts?.Cancel();
        }

        private async Task StartDeploymentAsync()
        {
            _cts = new CancellationTokenSource();
            _viewModel.IsDeploying = true;

            var galleryItem = _wizardData.SelectedItem;
            var vmSettings = _wizardData.Settings;
            var vmCustomizations = _wizardData.Customizations;

            // Validation (same guards as the old CreateVMAsync)
            if (galleryItem == null)
            {
                _viewModel.ErrorMessage = "No gallery item selected.";
                _viewModel.HasFailed = true;
                _viewModel.IsDeploying = false;
                return;
            }
            if (string.IsNullOrEmpty(vmSettings.VMName))
            {
                _viewModel.ErrorMessage = "VM Name is required.";
                _viewModel.HasFailed = true;
                _viewModel.IsDeploying = false;
                return;
            }
            if (string.IsNullOrEmpty(galleryItem.DiskUri) || !galleryItem.DiskUri.StartsWith("http"))
            {
                _viewModel.ErrorMessage = $"Invalid disk URI: {galleryItem.DiskUri}";
                _viewModel.HasFailed = true;
                _viewModel.IsDeploying = false;
                return;
            }

            // Append timestamp once, just before creation
            vmSettings.VMName = $"{vmSettings.VMName}_{DateTime.Now:yyyyMMddHHmmss}";
            _viewModel.VmName = vmSettings.VMName;

            // Activate the first phase
            ActivateNextPhase(DeployPageViewModel.PhaseDownload);

            try
            {
                var progressReport = new Progress<CreateVMProgressInfo>(OnProgressReport);
                await _createVM.StartCreateVMAsync(vmSettings, vmCustomizations, galleryItem, _cts.Token, progressReport);

                // Mark any remaining Active phase as Completed
                CompleteCurrentPhase();

                // Mark Done
                var donePhase = _viewModel.FindPhase(DeployPageViewModel.PhaseDone);
                if (donePhase != null)
                {
                    donePhase.Status = DeploymentPhaseStatus.Completed;
                    donePhase.ProgressText = $"VM \u2018{vmSettings.VMName}\u2019 created successfully!";
                }

                _viewModel.IsComplete = true;
                _viewModel.IsDeploying = false;
                _logger.LogInformation("Deployment completed successfully for {VMName}", vmSettings.VMName);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Deployment cancelled by user.");
                FailCurrentPhase("Cancelled by user.");
                _viewModel.ErrorMessage = "Deployment was cancelled.";
                _viewModel.HasFailed = true;
                _viewModel.IsDeploying = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deployment failed for {VMName}", vmSettings.VMName);
                FailCurrentPhase(ex.Message);
                _viewModel.ErrorMessage = $"Deployment failed: {ex.Message}";
                _viewModel.HasFailed = true;
                _viewModel.IsDeploying = false;
            }
        }

        // ── Progress report mapping ──────────────────────────────────────

        private void OnProgressReport(CreateVMProgressInfo info)
        {
            if (info == null) return;

            // Error reports from guest diagnostics — fail the current phase immediately
            if (!string.IsNullOrEmpty(info.ErrorMessage))
            {
                FailCurrentPhase(info.ErrorMessage);
                _logger.LogError("Deployment error reported: {Error}", info.ErrorMessage);
                if (!string.IsNullOrEmpty(info.DiagnosticsLog))
                    _logger.LogError("Full diagnostics:\n{Log}", info.DiagnosticsLog);
                return;
            }

            // Dynamically insert phase cards based on detected generation.
            // This is handled outside the phase-transition guard because the
            // generation info may arrive on a repeated "CreateVM" report
            // (the first report activates the phase; the second carries the
            // DetectedGeneration after partition detection completes).
            if (!string.IsNullOrEmpty(info.DetectedGeneration))
            {
                if (info.DetectedGeneration == "1")
                {
                    Application.Current.Dispatcher.Invoke(() => _viewModel.InsertMbrPhases());
                }
                else if (info.DetectedGeneration == "2")
                {
                    Application.Current.Dispatcher.Invoke(() => _viewModel.InsertCustomizePhase());
                }
            }

            // Phase transitions
            if (!string.IsNullOrEmpty(info.Phase))
            {
                string targetPhase = MapPhaseString(info.Phase);

                if (targetPhase != null && targetPhase != _activePhaseId)
                {
                    // Insert PostBoot phase card when transitioning to it
                    if (targetPhase == DeployPageViewModel.PhasePostBoot)
                    {
                        Application.Current.Dispatcher.Invoke(() => _viewModel.InsertPostBootPhase());
                    }

                    CompleteCurrentSubStep();
                    CompleteCurrentPhase();
                    ActivateNextPhase(targetPhase);
                }
            }

            // Sub-step activation for post-boot steps (driven by StepName)
            if (_activePhaseId == DeployPageViewModel.PhasePostBoot && !string.IsNullOrEmpty(info.StepName))
            {
                string subId = MapPostBootStepName(info.StepName);
                if (subId != null && subId != _activeSubStepId)
                {
                    CompleteCurrentSubStep();
                    _activeSubStepId = subId;
                    _viewModel.ActivatePhase(subId);
                }
            }

            // Sub-step activation for pre-boot (driven by KVP WorkflowProgress via URI)
            if (_activePhaseId == DeployPageViewModel.PhaseCustomize && !string.IsNullOrEmpty(info.URI))
            {
                string subId = MapPreBootProgress(info.URI);
                if (subId != null && subId != _activeSubStepId)
                {
                    CompleteCurrentSubStep();
                    _activeSubStepId = subId;
                    _viewModel.ActivatePhase(subId);
                }
            }

            // Update progress on the currently active phase
            if (_activePhaseId != null)
            {
                string progressText = null;

                if (info.DownloadSpeed > 0)
                    progressText = $"{info.DownloadSpeed:F2} MB/s";
                else if (!string.IsNullOrEmpty(info.StepName))
                    progressText = info.StepName;
                else if (!string.IsNullOrEmpty(info.URI))
                    progressText = info.URI;

                if (info.ProgressPercentage > 0)
                {
                    _viewModel.UpdatePhaseProgress(_activePhaseId, info.ProgressPercentage, progressText);
                }
                else if (progressText != null)
                {
                    var phase = _viewModel.FindPhase(_activePhaseId);
                    if (phase != null)
                        phase.ProgressText = progressText;
                }
            }
        }

        /// <summary>Maps the raw Phase strings from CreateVMProgressInfo to our well-known phase IDs.</summary>
        private static string MapPhaseString(string phase)
        {
            if (string.IsNullOrEmpty(phase)) return null;

            // Exact matches first
            return phase switch
            {
                DeployPageViewModel.PhaseDownload  => DeployPageViewModel.PhaseDownload,
                DeployPageViewModel.PhaseExtract   => DeployPageViewModel.PhaseExtract,
                DeployPageViewModel.PhaseConvert   => DeployPageViewModel.PhaseConvert,
                DeployPageViewModel.PhaseCreateVM  => DeployPageViewModel.PhaseCreateVM,
                DeployPageViewModel.PhaseStartVM   => DeployPageViewModel.PhaseStartVM,
                DeployPageViewModel.PhaseCloneDisk => DeployPageViewModel.PhaseCloneDisk,
                DeployPageViewModel.PhaseCustomize => DeployPageViewModel.PhaseCustomize,
                DeployPageViewModel.PhasePostBoot  => DeployPageViewModel.PhasePostBoot,
                _ => phase switch
                {
                    // Legacy / descriptive phase strings from existing code
                    var p when p.Contains("Extracting")   => DeployPageViewModel.PhaseExtract,
                    var p when p.Contains("Converting")   => DeployPageViewModel.PhaseConvert,
                    var p when p.Contains("non-sparse")   => DeployPageViewModel.PhaseConvert,
                    var p when p.Contains("Waiting for VM")      => DeployPageViewModel.PhaseCloneDisk,
                    var p when p.Contains("disk clone")          => DeployPageViewModel.PhaseCloneDisk,
                    var p when p.Contains("Cloning")             => DeployPageViewModel.PhaseCloneDisk,
                    var p when p.Contains("customizations")      => DeployPageViewModel.PhaseCustomize,
                    _ => null
                }
            };
        }

        private void ActivateNextPhase(string phaseId)
        {
            if (phaseId == null) return;
            _activePhaseId = phaseId;
            _viewModel.ActivatePhase(phaseId);
        }

        private void CompleteCurrentPhase()
        {
            if (_activePhaseId != null)
            {
                // Complete any lingering sub-step along with the parent
                CompleteCurrentSubStep();
                _viewModel.CompletePhase(_activePhaseId);
            }
        }

        private void CompleteCurrentSubStep()
        {
            if (_activeSubStepId != null)
            {
                _viewModel.CompletePhase(_activeSubStepId);
                _activeSubStepId = null;
            }
        }

        private void FailCurrentPhase(string message)
        {
            if (_activePhaseId != null)
            {
                _viewModel.FailPhase(_activePhaseId, message);
            }
        }

        /// <summary>Maps ICustomizationStep.Name to a post-boot sub-step card ID.</summary>
        private static string MapPostBootStepName(string stepName) => stepName switch
        {
            "Remove VirtualBox Guest Additions" => DeployPageViewModel.SubRemoveVBox,
            "Sync Timezone"                     => DeployPageViewModel.SubSyncTimezone,
            "Install OpenVPN"                   => DeployPageViewModel.SubConfigureVpn,
            "Deploy VPN Configs"                => DeployPageViewModel.SubConfigureVpn, // collapsed into one card
            "Restore SSH State"                 => DeployPageViewModel.SubRestoreSsh,
            _ => null
        };

        /// <summary>Maps KVP WorkflowProgress prefix to a pre-boot sub-step card ID.</summary>
        private static string MapPreBootProgress(string progress)
        {
            if (progress.StartsWith("INSTALL_GRUB", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.SubInstallGrub;
            if (progress.StartsWith("INSTALL_HYPERV", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.SubInstallHyperV;
            if (progress.StartsWith("INSTALL_XRDP", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.SubInstallXrdp;
            if (progress.StartsWith("INSTALL_PWSH", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.SubInstallPwsh;
            if (progress.StartsWith("SSH_SETUP", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.SubSshSetup;
            if (progress.StartsWith("REBOOT", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.SubReboot;
            return null;
        }
    }
}
