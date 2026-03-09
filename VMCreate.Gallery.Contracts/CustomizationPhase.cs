namespace VMCreate
{
    /// <summary>
    /// Identifies when a customization step runs relative to the VM lifecycle.
    /// </summary>
    public enum CustomizationPhase
    {
        /// <summary>Applied during ISO chroot before the VM's first boot (e.g. xRDP install).</summary>
        PreBoot,

        /// <summary>Applied via SSH after the VM boots from its own hard drive (e.g. timezone, VPN).</summary>
        PostBoot
    }
}
