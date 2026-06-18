namespace VMCreate
{
    /// <summary>
    /// Adapter that exposes the subset of <see cref="DeployPageViewModel"/> used by
    /// <see cref="DeploymentProgressPresenter"/> through <see cref="IDeploymentProgressViewModel"/>.
    /// </summary>
    public sealed class DeploymentProgressViewModelAdapter : IDeploymentProgressViewModel
    {
        private readonly DeployPageViewModel _viewModel;

        public DeploymentProgressViewModelAdapter(DeployPageViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
        }

        public string VmName
        {
            set => _viewModel.VmName = value;
        }

        public void InsertDownloadCloningIsoPhase() => _viewModel.InsertDownloadCloningIsoPhase();
        public void InsertPostBootPhase() => _viewModel.InsertPostBootPhase();
        public void InsertMbrPhases() => _viewModel.InsertMbrPhases();
        public void InsertDiskSubSteps(int detectedGeneration, bool needsIsoBoot)
            => _viewModel.InsertDiskSubSteps(detectedGeneration, needsIsoBoot);
        public void InsertCustomizePhase() => _viewModel.InsertCustomizePhase();
        public void InsertCleanupIsoBootPhase() => _viewModel.InsertCleanupIsoBootPhase();

        public void ActivatePhase(string id) => _viewModel.ActivatePhase(id);
        public void CompletePhase(string id) => _viewModel.CompletePhase(id);
        public void FailPhase(string id, string message) => _viewModel.FailPhase(id, message);

        public void UpdatePhaseProgress(string id, int percentage, string progressText)
            => _viewModel.UpdatePhaseProgress(id, percentage, progressText);
    }
}
