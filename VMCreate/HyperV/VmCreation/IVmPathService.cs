namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Resolves the default Hyper-V VM and VHDX storage paths from the host registry.
    /// </summary>
    public interface IVmPathService
    {
        /// <summary>
        /// Default directory where Hyper-V stores virtual machine configuration files.
        /// </summary>
        string DefaultVmPath { get; }

        /// <summary>
        /// Default directory where Hyper-V stores virtual hard disk files.
        /// </summary>
        string DefaultVhdxPath { get; }

        /// <summary>
        /// Returns the per-VM subdirectory under <see cref="DefaultVhdxPath"/>.
        /// </summary>
        string GetVirtualHardDiskPath(string vmName);

        /// <summary>
        /// Checks whether the current user is a member of the local
        /// "Hyper-V Administrators" group (well-known SID S-1-5-32-578).
        /// This group grants WMI namespace access required for VM creation.
        /// </summary>
        bool IsHyperVAdministrator();
    }
}
