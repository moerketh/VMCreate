namespace VMCreate
{
    /// <summary>
    /// Deployment-page card metadata for a customization step. Implemented by
    /// steps so the main application can render a progress card without
    /// hard-coding the step's title, description, or icon there.
    /// <para>
    /// <see cref="ICustomizationStep.ProgressPhaseId"/> (when set) is the card
    /// ID; steps without one get a stable name-derived ID from
    /// <c>PostBootStepPlanner.CardId</c>. When this interface is absent, the
    /// deployment page falls back to <see cref="ICustomizationStep.Name"/> and
    /// a generic description.
    /// </para>
    /// </summary>
    public interface IStepDisplayInfo
    {
        /// <summary>Card title shown on the deployment page.</summary>
        string DeployTitle { get; }

        /// <summary>Card description shown below the title.</summary>
        string DeployDescription { get; }

        /// <summary>
        /// WPF UI SymbolRegular icon name as a string (e.g. "Hourglass24", "Shield24").
        /// The main app resolves this to a <see cref="Wpf.Ui.Controls.SymbolRegular"/> value.
        /// </summary>
        string DeployIconName { get; }
    }
}