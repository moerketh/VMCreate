namespace VMCreate
{
    /// <summary>
    /// Identifies the guest operating system a customization step targets.
    /// The orchestrator only runs a step whose <see cref="StepPlatform"/> matches the
    /// VM being deployed, so Linux steps never run over PowerShell Direct and Windows
    /// steps never run over SSH.
    /// </summary>
    public enum StepPlatform
    {
        /// <summary>Runs against Windows guests via PowerShell Direct.</summary>
        Windows,

        /// <summary>Runs against Linux guests via SSH.</summary>
        Linux
    }
}
