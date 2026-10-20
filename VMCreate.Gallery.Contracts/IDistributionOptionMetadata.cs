namespace VMCreate
{
    /// <summary>
    /// Optional metadata supplied by a distribution-specific customization step so the
    /// deployment UI can render a progress card without hard-coding the step's name,
    /// description, or icon in the main application.
    /// </summary>
    public interface IDistributionOptionMetadata : IStepDisplayInfo
    {
        /// <summary>
        /// Optional fixed card ID used for both the deployment page phase list and
        /// runtime progress reporting. When null/empty, the deployment page falls back
        /// to <c>PostBootStepPlanner.CardId(Name)</c>.
        /// </summary>
        string DeployPhaseId { get; }

        /// <summary>
        /// Optional message shown after deployment completes, when this step is enabled.
        /// Use it for long-running background tasks that outlast the visible deployment
        /// (e.g. "Please allow at least one hour for the FLARE VM scripts to finish...").
        /// When null/empty, no extra completion card is rendered.
        /// </summary>
        string? DeployCompletionInfo { get; }
    }
}
