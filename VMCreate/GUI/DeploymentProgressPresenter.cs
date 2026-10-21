using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV.VmCreation;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    /// <summary>
    /// Maps backend <see cref="CreateVMProgressInfo"/> reports to the UI phase model
    /// maintained by <see cref="DeployPageViewModel"/>.  Keeps view code-behind focused
    /// on view concerns (scrolling, layout, lifecycle) instead of progress semantics.
    /// </summary>
    public interface IDeploymentProgressPresenter
    {
        /// <summary>
        /// The currently active top-level phase ID, or null if none has been activated yet.
        /// </summary>
        string? ActivePhaseId { get; }

        /// <summary>
        /// The currently active sub-step phase ID, or null if none is active.
        /// </summary>
        string? ActiveSubStepId { get; }

        /// <summary>
        /// Processes a single progress report and applies changes to the view model.
        /// Returns the phase/sub-step IDs that were activated (if any) so the view
        /// can scroll to them.
        /// </summary>
        ProgressPresentationResult Present(CreateVMProgressInfo? info);

        /// <summary>
        /// Completes the currently active phase and any active sub-step.
        /// </summary>
        void CompleteActive();

        /// <summary>
        /// Marks the active phase as failed with the supplied message.
        /// </summary>
        void FailActive(string message);
    }

    /// <summary>
    /// Result of presenting one progress report.
    /// </summary>
    public sealed class ProgressPresentationResult
    {
        /// <summary>
        /// Phase or sub-step ID that should be scrolled into view, if any.
        /// </summary>
        public string? ScrollToId { get; set; }

        /// <summary>
        /// True when the report contained an error and the deployment should stop.
        /// </summary>
        public bool IsError { get; set; }
    }

    /// <summary>
    /// Default implementation of <see cref="IDeploymentProgressPresenter"/>.
    /// </summary>
    public class DeploymentProgressPresenter : IDeploymentProgressPresenter
    {
        private readonly IDeploymentProgressViewModel _viewModel;
        private readonly GalleryItem _selectedItem;
        private readonly VmCustomizations _customizations;
        private readonly IReadOnlyDictionary<string, ICustomizationStep> _allSteps;
        private readonly ILogger _logger;
        private readonly IDispatcher _dispatcher;

        public DeploymentProgressPresenter(
            IDeploymentProgressViewModel viewModel,
            IDispatcher dispatcher,
            GalleryItem selectedItem,
            VmCustomizations customizations,
            IReadOnlyDictionary<string, ICustomizationStep> allSteps,
            ILogger logger)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _selectedItem = selectedItem;
            _customizations = customizations;
            _allSteps = allSteps ?? new Dictionary<string, ICustomizationStep>(StringComparer.OrdinalIgnoreCase);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string? ActivePhaseId { get; private set; }
        public string? ActiveSubStepId { get; private set; }

        public ProgressPresentationResult Present(CreateVMProgressInfo? info)
        {
            if (info == null) return new ProgressPresentationResult();

            if (!string.IsNullOrEmpty(info.ErrorMessage))
            {
                _logger.LogError("Deployment error reported: {Error}", info.ErrorMessage);
                if (!string.IsNullOrEmpty(info.DiagnosticsLog))
                    _logger.LogError("Full diagnostics:\n{Log}", info.DiagnosticsLog);

                FailActive(info.ErrorMessage);
                return new ProgressPresentationResult { IsError = true };
            }

            var result = new ProgressPresentationResult();

            if (!string.IsNullOrEmpty(info.VmName))
            {
                _dispatcher.Invoke(() => _viewModel.VmName = info.VmName);
            }

            if (info.DetectedGeneration.HasValue
                && _selectedItem?.IsNativeHyperV != true
                && DiskFileDetector.DetectFileType(_selectedItem?.DiskUri) != DiskImageFormat.Iso)
            {
                _dispatcher.Invoke(() => HandleDetectedGeneration(info.DetectedGeneration.Value));
            }

            // Phase transitions
            if (info.Phase != VmDeploymentPhase.None)
            {
                string? targetPhase = MapPhase(info.Phase);
                if (!string.IsNullOrEmpty(targetPhase) && targetPhase != ActivePhaseId)
                {
                    _dispatcher.Invoke(() =>
                    {
                        if (targetPhase == DeployPageViewModel.PhaseDownloadCloningIso)
                            _viewModel.InsertDownloadCloningIsoPhase();
                        else if (targetPhase == DeployPageViewModel.PhasePostBoot)
                            _viewModel.InsertPostBootPhase();

                        CompleteActiveSubStep();
                        CompleteActivePhase();
                        ActivatePhase(targetPhase);
                    });

                    result.ScrollToId = targetPhase;
                    ActivePhaseId = targetPhase;
                }
            }

            // Post-boot sub-step by step name
            if (ActivePhaseId == DeployPageViewModel.PhasePostBoot && !string.IsNullOrEmpty(info.StepName))
            {
                // Reconcile the card list against the planner's current plan
                // BEFORE mapping this report: the Auto RDP resolver (order
                // 232) mutates the shared customizations in place, so steps
                // become applicable/inapplicable mid-run (the chosen branch's
                // installs appear; the losing branch's pending cards are
                // removed). The service reports a step BEFORE executing it,
                // so on the resolver's own report the backend is still Auto
                // and reconciliation is a no-op; it takes effect on the next
                // applicable step's report, in time for its card to activate.
                _dispatcher.Invoke(() => _viewModel.ReconcilePostBootSteps());

                string? subId = MapPostBootStepName(info.StepName);
                if (!string.IsNullOrEmpty(subId) && subId != ActiveSubStepId)
                {
                    _dispatcher.Invoke(() =>
                    {
                        CompleteActiveSubStep();
                        ActivateSubStep(subId);
                    });
                    result.ScrollToId = subId;
                }
            }

            // Chroot (Customize-phase) steps report step names too, and the
            // resolver now flips the backend DURING the chroot — the same
            // reconciliation must run there so the losing branch's pending
            // chroot cards are removed before the user scrolls past them.
            if (ActivePhaseId == DeployPageViewModel.PhaseCustomize && !string.IsNullOrEmpty(info.StepName))
            {
                _dispatcher.Invoke(() => _viewModel.ReconcilePostBootSteps());
            }

            // Pre-boot sub-step by step name: the ISO stage reports chroot
            // steps (Phase=Customize, StepName=step.Name) and the scenario
            // scripts' VMCREATE:MSG texts land here too (StepName=message).
            // v1's URI-keyed mapping is gone — the v2 protocol never set URI.
            if (ActivePhaseId == DeployPageViewModel.PhaseCustomize && !string.IsNullOrEmpty(info.StepName))
            {
                string? subId = MapPreBootStepName(info.StepName);
                if (!string.IsNullOrEmpty(subId) && subId != ActiveSubStepId)
                {
                    _dispatcher.Invoke(() =>
                    {
                        CompleteActiveSubStep();
                        ActivateSubStep(subId);
                    });
                    result.ScrollToId = subId;
                }
            }

            // Sub-step by typed SubStep enum
            if (info.SubStep != VmDeploymentSubStep.None)
            {
                string? subStepId = MapSubStep(info.SubStep);
                if (!string.IsNullOrEmpty(subStepId) && subStepId != ActiveSubStepId)
                {
                    _dispatcher.Invoke(() =>
                    {
                        CompleteActiveSubStep();
                        ActivateSubStep(subStepId);
                    });
                    result.ScrollToId = subStepId;
                }
            }

            // Update progress on the currently active phase
            UpdateProgress(info);

            return result;
        }

        public void CompleteActive()
        {
            _dispatcher.Invoke(() =>
            {
                CompleteActiveSubStep();
                CompleteActivePhase();
            });
        }

        public void FailActive(string message)
        {
            _dispatcher.Invoke(() =>
            {
                if (ActiveSubStepId != null)
                    _viewModel.FailPhase(ActiveSubStepId, message);
                else if (ActivePhaseId != null)
                    _viewModel.FailPhase(ActivePhaseId, message);
            });
        }

        private void HandleDetectedGeneration(int generation)
        {
            // needsIsoBoot mirrors DiskImageVmCreationStrategy via the
            // planner's shared predicate: the cloning ISO cycle runs when
            // xrdp is selected (pre-boot install) OR any Linux post-boot
            // step applies (they need the automation-SSH path the ISO cycle
            // provisions — chroot-capable steps run inside the ISO stage
            // itself). One predicate for backend and UI so the card list
            // can't diverge from the boot decision.
            bool needsIsoBoot = PostBootStepPlanner.NeedsIsoBootCycle(
                _selectedItem, _customizations, _allSteps.Values);

            if (generation == 1)
            {
                _viewModel.InsertMbrPhases();
                _viewModel.InsertDiskSubSteps(1, needsIsoBoot: true);
            }
            else if (generation == 2)
            {
                _viewModel.InsertCustomizePhase();
                _viewModel.InsertDiskSubSteps(2, needsIsoBoot);
            }
        }

        private void ActivatePhase(string phaseId)
        {
            ActivePhaseId = phaseId;
            _viewModel.ActivatePhase(phaseId);
        }

        private void ActivateSubStep(string subStepId)
        {
            ActiveSubStepId = subStepId;
            _viewModel.ActivatePhase(subStepId);
        }

        private void CompleteActivePhase()
        {
            if (ActivePhaseId != null)
            {
                _viewModel.CompletePhase(ActivePhaseId);
            }
        }

        private void CompleteActiveSubStep()
        {
            if (ActiveSubStepId != null)
            {
                _viewModel.CompletePhase(ActiveSubStepId);
                ActiveSubStepId = null;
            }
        }

        private void UpdateProgress(CreateVMProgressInfo info)
        {
            if (ActivePhaseId == null) return;

            string? progressText = null;
            if (info.DownloadSpeed > 0)
                progressText = $"{info.DownloadSpeed:F2} MB/s";
            else if (!string.IsNullOrEmpty(info.StepName))
                progressText = info.StepName;
            else if (!string.IsNullOrEmpty(info.URI))
                progressText = info.URI;

            _dispatcher.Invoke(() =>
            {
                string targetId = ActiveSubStepId ?? ActivePhaseId;
                if (info.ProgressPercentage > 0)
                {
                    _viewModel.UpdatePhaseProgress(targetId, info.ProgressPercentage, progressText);
                }
                else if (progressText != null)
                {
                    _viewModel.UpdatePhaseProgress(targetId, 0, progressText);
                }
            });
        }

        private static string? MapPhase(VmDeploymentPhase phase)
        {
            return phase switch
            {
                VmDeploymentPhase.Download => DeployPageViewModel.PhaseDownload,
                VmDeploymentPhase.Extract => DeployPageViewModel.PhaseExtract,
                VmDeploymentPhase.Convert => DeployPageViewModel.PhaseConvert,
                VmDeploymentPhase.DownloadCloningIso => DeployPageViewModel.PhaseDownloadCloningIso,
                VmDeploymentPhase.CreateVM => DeployPageViewModel.PhaseCreateVM,
                VmDeploymentPhase.StartVM => DeployPageViewModel.PhaseStartVM,
                VmDeploymentPhase.Customize => DeployPageViewModel.PhaseCustomize,
                VmDeploymentPhase.PostBoot => DeployPageViewModel.PhasePostBoot,
                _ => null
            };
        }

        private static string? MapSubStep(VmDeploymentSubStep subStep)
        {
            return subStep switch
            {
                VmDeploymentSubStep.CreateVMSkeleton => DeployPageViewModel.SubCreateVMSkeleton,
                VmDeploymentSubStep.ConnectNic => DeployPageViewModel.SubConnectNic,
                VmDeploymentSubStep.ConfigureHardware => DeployPageViewModel.SubConfigureHardware,
                VmDeploymentSubStep.AttachDisk => DeployPageViewModel.SubAttachDisk,
                VmDeploymentSubStep.AttachCloneDisk => DeployPageViewModel.SubAttachCloneDisk,
                VmDeploymentSubStep.AttachBootDvd => DeployPageViewModel.SubAttachBootDvd,
                VmDeploymentSubStep.SetBootOrder => DeployPageViewModel.SubSetBootOrder,
                VmDeploymentSubStep.EnableNestedVirt => DeployPageViewModel.SubEnableNestedVirt,
                VmDeploymentSubStep.AddTempNic => DeployPageViewModel.SubAddTempNic,
                VmDeploymentSubStep.WaitForSsh => DeployPageViewModel.SubWaitForSsh,
                VmDeploymentSubStep.WaitForKvpHeartbeat => DeployPageViewModel.SubWaitForKvpHeartbeat,
                _ => null
            };
        }

        private string? MapPostBootStepName(string stepName)
        {
            if (_allSteps.TryGetValue(stepName, out var step) && step != null)
            {
                return PostBootStepPlanner.CardId(step);
            }

            // Unknown step name (not in the DI step set): produce the same
            // name-derived ID the planner would, so a report from a step the
            // front end did not register still maps consistently.
            return "Sub_Step_" + stepName;
        }

        /// <summary>
        /// Maps a Customize-phase step report to its card: first by exact
        /// step name (chroot steps report their ICustomizationStep.Name and
        /// get the planner's CardId), then by the scenario scripts' known
        /// progress messages (VMCREATE:MSG texts like "Installing Hyper-V
        /// guest integration services" from the ISO stage).
        /// </summary>
        private string? MapPreBootStepName(string stepName)
        {
            if (_allSteps.TryGetValue(stepName, out var step) && step != null)
                return PostBootStepPlanner.CardId(step);

            if (stepName.Contains("cloning root", StringComparison.OrdinalIgnoreCase)
                || stepName.Contains("disk partitioning", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.PhaseCloneDisk;
            if (stepName.Contains("GRUB", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.SubInstallGrub;
            if (stepName.Contains("Hyper-V guest", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.SubInstallHyperV;
            if (stepName.Contains("XRDP", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.SubInstallXrdp;
            if (stepName.Contains("automation user", StringComparison.OrdinalIgnoreCase)
                || stepName.Contains("SSH key", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.SubSshSetup;
            if (stepName.Contains("Shutting down", StringComparison.OrdinalIgnoreCase))
                return DeployPageViewModel.SubReboot;

            // Unknown step name (not registered in the front end): the same
            // name-derived ID the planner would produce, so a chroot-capable
            // step the GUI didn't pre-register still maps consistently.
            return "Sub_Step_" + stepName;
        }
    }
}
