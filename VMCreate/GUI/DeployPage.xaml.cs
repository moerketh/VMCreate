using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VMCreate
{
    public partial class DeployPage : Page
    {
        public event EventHandler<WizardResultEventArgs> WizardCompleted;

        private readonly DeployPageViewModel _viewModel;
        private readonly WizardData _wizardData;
        private readonly CreateVM _createVM;
        private readonly ILogger _logger;
        private readonly IDeploymentProgressPresenter _presenter;
        private CancellationTokenSource _cts;
        private bool _autoScrollEnabled = true;
        private bool _isScrollingProgrammatically;
        private string _effectiveVmName;

        public DeployPage(WizardData wizardData, CreateVM createVM, ILoggerFactory loggerFactory, IEnumerable<IConfigurableCustomizationStep> configurableSteps, IReadOnlyDictionary<string, ICustomizationStep> allSteps)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _createVM = createVM ?? throw new ArgumentNullException(nameof(createVM));
            if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));

            _logger = loggerFactory.CreateLogger<DeployPage>();
            _viewModel = new DeployPageViewModel(wizardData, _logger, configurableSteps);
            _presenter = new DeploymentProgressPresenter(
                new DeploymentProgressViewModelAdapter(_viewModel),
                new WpfDispatcher(),
                wizardData.SelectedItem,
                wizardData.Customizations,
                allSteps,
                _logger);

            InitializeComponent();
            DataContext = _viewModel;

            _viewModel.RequestCancel += OnCancelRequested;
            _viewModel.RequestWizardComplete += result =>
                WizardCompleted?.Invoke(this, new WizardResultEventArgs(result));

            Loaded += async (_, __) =>
            {
                PhaseScrollViewer.ScrollChanged += OnScrollChanged;
                await StartDeploymentAsync();
            };
            Unloaded += (_, __) =>
            {
                PhaseScrollViewer.ScrollChanged -= OnScrollChanged;
                Cleanup();
            };
        }

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

            if (galleryItem == null)
            {
                SetError("No gallery item selected.");
                return;
            }
            if (string.IsNullOrEmpty(vmSettings.VMName))
            {
                SetError("VM Name is required.");
                return;
            }
            if (string.IsNullOrEmpty(galleryItem.DiskUri) || !galleryItem.DiskUri.StartsWith("http"))
            {
                SetError($"Invalid disk URI: {galleryItem.DiskUri}");
                return;
            }

            _presenter.CompleteActive();
            _viewModel.ActivatePhase(DeployPageViewModel.PhaseDownload);
            ScrollToPhase(DeployPageViewModel.PhaseDownload);

            try
            {
                var progressReport = new Progress<CreateVMProgressInfo>(OnProgressReport);
                string effectiveVmName = await _createVM.StartCreateVMAsync(vmSettings, vmCustomizations, galleryItem, _cts.Token, progressReport);
                _effectiveVmName = effectiveVmName;

                _presenter.CompleteActive();

                var donePhase = _viewModel.FindPhase(DeployPageViewModel.PhaseDone);
                if (donePhase != null)
                {
                    donePhase.Status = DeploymentPhaseStatus.Completed;
                    donePhase.ProgressText = $"VM \u2018{effectiveVmName}\u2019 created successfully!";
                }

                _viewModel.IsComplete = true;
                _viewModel.IsDeploying = false;
                BottomSpacer.Height = 0;
                ScrollToPhase(DeployPageViewModel.PhaseDone);
                _logger.LogInformation("Deployment completed successfully for {VMName}", effectiveVmName);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Deployment cancelled by user.");
                _presenter.FailActive("Cancelled by user.");
                SetError("Deployment was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deployment failed for {VMName}", vmSettings.VMName);
                _presenter.FailActive(ex.Message);
                SetError($"Deployment failed: {ex.Message}");
            }
        }

        private void SetError(string message)
        {
            _viewModel.ErrorMessage = message;
            _viewModel.HasFailed = true;
            _viewModel.IsDeploying = false;
            BottomSpacer.Height = 0;
        }

        private void OnProgressReport(CreateVMProgressInfo info)
        {
            if (!string.IsNullOrEmpty(info.VmName))
                _effectiveVmName = info.VmName;

            var result = _presenter.Present(info);
            if (result.IsError)
            {
                _viewModel.ErrorMessage = info.ErrorMessage;
                _viewModel.HasFailed = true;
                _viewModel.IsDeploying = false;
                BottomSpacer.Height = 0;
                return;
            }

            if (result.ScrollToId != null)
                ScrollToPhase(result.ScrollToId);

            if (result.ScrollToId == DeployPageViewModel.PhaseStartVM && _wizardData.DemoMode)
                LaunchVmConnect();
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isScrollingProgrammatically) return;
            if (e.ExtentHeightChange != 0) return;
            if (e.ViewportHeightChange != 0) return;
            if (e.VerticalChange < 0)
                _autoScrollEnabled = false;
        }

        private void ScrollToPhase(string phaseId)
        {
            if (!_autoScrollEnabled || phaseId == null) return;

            _isScrollingProgrammatically = true;

            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var phase = _viewModel.FindPhase(phaseId);
                    if (phase == null) return;

                    int index = _viewModel.Phases.IndexOf(phase);
                    if (index < 0) return;

                    var container = PhaseItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                    if (container == null) return;

                    var transform = container.TransformToAncestor(PhaseScrollViewer);
                    var point = transform.Transform(new Point(0, 0));
                    double targetOffset = PhaseScrollViewer.VerticalOffset + point.Y;

                    if (index > 0)
                    {
                        var prev = PhaseItemsControl.ItemContainerGenerator.ContainerFromIndex(index - 1) as FrameworkElement;
                        if (prev != null)
                            targetOffset -= prev.ActualHeight;
                    }

                    PhaseScrollViewer.ScrollToVerticalOffset(Math.Max(0, targetOffset));
                }
                finally
                {
                    Dispatcher.InvokeAsync(() => _isScrollingProgrammatically = false, DispatcherPriority.Input);
                }
            }, DispatcherPriority.Loaded);
        }

        private void LaunchVmConnect()
        {
            try
            {
                var vmName = _effectiveVmName ?? _wizardData.Settings?.VMName;
                if (string.IsNullOrEmpty(vmName)) return;

                _logger.LogInformation("Demo mode: launching VMConnect for {VMName}", vmName);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "vmconnect.exe", $"localhost \"{vmName}\"")
                { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch vmconnect.exe in demo mode");
            }
        }
    }
}
