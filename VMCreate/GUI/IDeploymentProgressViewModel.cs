namespace VMCreate
{
    /// <summary>
    /// View-model surface used by <see cref="DeploymentProgressPresenter"/>.
    /// Extracted so phase-mapping logic can be unit-tested with an in-memory fake.
    /// </summary>
    public interface IDeploymentProgressViewModel
    {
        /// <summary>
        /// Sets the effective VM name displayed during deployment.
        /// </summary>
        string VmName { set; }

        void InsertDownloadCloningIsoPhase();
        void InsertPostBootPhase();
        void InsertMbrPhases();
        void InsertDiskSubSteps(int detectedGeneration, bool needsIsoBoot);
        void InsertCustomizePhase();

        /// <summary>
        /// Reconciles the post-boot card list with the planner's current plan —
        /// inserts cards for newly applicable steps, removes pending cards whose
        /// step became inapplicable. Safe to call on every post-boot step report;
        /// see <see cref="DeployPageViewModel.ReconcilePostBootSteps"/>.
        /// </summary>
        void ReconcilePostBootSteps();

        void ActivatePhase(string id);
        void CompletePhase(string id);
        void FailPhase(string id, string message);

        void UpdatePhaseProgress(string id, int percentage, string? progressText);
    }
}
