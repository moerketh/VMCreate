namespace VMCreate
{
    /// <summary>
    /// Identifies when a customization step runs relative to the VM lifecycle.
    /// Pre-boot (ISO-chroot) execution is expressed per step via
    /// <see cref="ICustomizationStep.SupportsChroot"/> instead of a phase
    /// value: a chroot-capable step still belongs to the post-boot pipeline
    /// and merely runs earlier when the deployment has an ISO stage.
    /// </summary>
    public enum CustomizationPhase
    {
        /// <summary>
        /// Applied via SSH after the VM boots from its own hard drive. Steps
        /// marked <see cref="ICustomizationStep.SupportsChroot"/> may instead
        /// execute pre-boot inside the ISO stage's chroot of the target
        /// filesystem, behind the plymouth splash, before the user can reach
        /// the VM.
        /// </summary>
        PostBoot
    }
}
