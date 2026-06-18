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
        void InsertCleanupIsoBootPhase();

        void ActivatePhase(string id);
        void CompletePhase(string id);
        void FailPhase(string id, string message);

        void UpdatePhaseProgress(string id, int percentage, string progressText);
    }
}
