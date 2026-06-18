namespace VMCreate
{
    /// <summary>
    /// High-level phases of a VM deployment. The UI maps these to phase cards.
    /// </summary>
    public enum VmDeploymentPhase
    {
        None,
        Download,
        Extract,
        Convert,
        DownloadCloningIso,
        CreateVM,
        StartVM,
        Customize,
        PostBoot,
        Finished,
        Failed
    }

    /// <summary>
    /// Well-known sub-steps within a phase. Free-form/dynamic sub-step identifiers
    /// can still be reported via <see cref="CreateVMProgressInfo.StepName"/> or
    /// <see cref="CreateVMProgressInfo.URI"/>.
    /// </summary>
    public enum VmDeploymentSubStep
    {
        None,
        CreateVMSkeleton,
        ConnectNic,
        ConfigureHardware,
        AttachDisk,
        AttachCloneDisk,
        AttachBootDvd,
        SetBootOrder,
        EnableNestedVirt,
        CleanupIsoBoot,
        AddTempNic,
        WaitForSsh
    }

    /// <summary>
    /// Progress payload reported during VM creation.
    /// Uses typed <see cref="VmDeploymentPhase"/> and <see cref="VmDeploymentSubStep"/>
    /// to keep the contract strongly-typed, while still carrying free-form text fields
    /// (URI, StepName, VmName) for dynamic progress details.
    /// </summary>
    public sealed class CreateVMProgressInfo
    {
        public VmDeploymentPhase Phase { get; init; } = VmDeploymentPhase.None;
        public string URI { get; init; }
        public int ProgressPercentage { get; init; }
        public double DownloadSpeed { get; init; }

        /// <summary>
        /// 1 for MBR/BIOS, 2 for UEFI/GPT. Null when generation has not been detected.
        /// </summary>
        public int? DetectedGeneration { get; init; }

        /// <summary>
        /// Error message from the guest (collected via SSH or PowerShell Direct).
        /// When set, the current phase should transition to Failed.
        /// </summary>
        public string ErrorMessage { get; init; }

        /// <summary>
        /// Full diagnostic log from the guest (journal, service status, dmesg).
        /// Only populated when an error is detected and diagnostics are collected.
        /// </summary>
        public string DiagnosticsLog { get; init; }

        /// <summary>
        /// Name of the current customization step being executed (e.g. "Sync Timezone").
        /// Used by the Deploy page to show per-step progress text.
        /// </summary>
        public string StepName { get; init; }

        /// <summary>
        /// Effective VM name for this deployment. Reported by the creator once the
        /// timestamped name is known so the UI can display it without mutating
        /// the original settings.
        /// </summary>
        public string VmName { get; init; }

        /// <summary>
        /// Identifies a well-known sub-step within the current phase (e.g. ConnectNic during CreateVM).
        /// </summary>
        public VmDeploymentSubStep SubStep { get; init; } = VmDeploymentSubStep.None;

        /// <summary>
        /// Factory helper for the common case: report a phase with an optional sub-step and VM name.
        /// </summary>
        public static CreateVMProgressInfo ForPhase(
            VmDeploymentPhase phase,
            VmDeploymentSubStep subStep = VmDeploymentSubStep.None,
            string vmName = null)
            => new CreateVMProgressInfo { Phase = phase, SubStep = subStep, VmName = vmName };

        /// <summary>
        /// Factory helper for progress within a phase.
        /// </summary>
        public static CreateVMProgressInfo ForProgress(VmDeploymentPhase phase, int percentage, string uri = null, double downloadSpeed = 0)
            => new CreateVMProgressInfo { Phase = phase, ProgressPercentage = percentage, URI = uri, DownloadSpeed = downloadSpeed };
    }
}
