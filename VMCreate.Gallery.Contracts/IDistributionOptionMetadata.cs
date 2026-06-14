namespace VMCreate
{
    /// <summary>
    /// Optional metadata supplied by a distribution-specific customization step so the
    /// deployment UI can render a progress card without hard-coding the step's name,
    /// description, or icon in the main application.
    /// </summary>
    public interface IDistributionOptionMetadata
    {
        /// <summary>
        /// Card title shown on the deployment page (e.g. "Install FLARE VM").
        /// Defaults to <see cref="ICustomizationStep.Name"/> when absent or empty.
        /// </summary>
        string DeployTitle { get; }

        /// <summary>
        /// Card description shown below the title.</summary>
        string DeployDescription { get; }

        /// <summary>
        /// Optional fixed card ID used for both the deployment page phase list and
        /// runtime progress reporting. When null/empty, the deployment page falls back
        /// to <c>DistOptionSubId(Name)</c>.
        /// </summary>
        string DeployPhaseId { get; }

        /// <summary>
        /// WPF UI SymbolRegular icon name as a string (e.g. "Hourglass24", "Shield24").
        /// The main app resolves this to a <see cref="Wpf.Ui.Controls.SymbolRegular"/> value.
        /// </summary>
        string DeployIconName { get; }

        /// <summary>
        /// Optional message shown after deployment completes, when this step is enabled.
        /// Use it for long-running background tasks that outlast the visible deployment
        /// (e.g. "Please allow at least one hour for the FLARE VM scripts to finish...").
        /// When null/empty, no extra completion card is rendered.
        /// </summary>
        string? DeployCompletionInfo { get; }

        /// <summary>
        /// Display order for deployment cards within the post-boot phase.
        /// Lower values appear earlier.
        /// </summary>
        int DeployOrder { get; }
    }
}
