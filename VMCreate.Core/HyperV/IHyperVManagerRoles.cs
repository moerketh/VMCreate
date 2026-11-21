using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// VM lifecycle operations: create, start, stop, remove, and find VMs.
    /// </summary>
    public interface IVmLifecycleManager
    {
        Task CreateVMAsync(VmDeploymentPlan plan, string vmPath, int targetGeneration, CancellationToken cancellationToken);
        Task StartVM(VmDeploymentPlan plan, CancellationToken cancellationToken);
        Task<string[]> FindExistingVmsByBaseNameAsync(string baseName, CancellationToken cancellationToken);
        Task StopVMAsync(string vmName, CancellationToken cancellationToken);
        Task RemoveVMAsync(string vmName, CancellationToken cancellationToken);

        /// <summary>
        /// True when a VM with exactly this name is registered with Hyper-V.
        /// Failure cleanup asks Hyper-V instead of tracking a flag: a strategy
        /// that throws after New-VM still leaves a registered VM behind.
        /// </summary>
        Task<bool> VmExistsAsync(string vmName, CancellationToken cancellationToken);

        /// <summary>
        /// Takes a checkpoint of the VM under the given name, using the VM's
        /// configured checkpoint type (Production, falling back to Standard
        /// when the guest cannot quiesce). Throws when Hyper-V reports an error.
        /// </summary>
        Task CreateCheckpointAsync(string vmName, string checkpointName, CancellationToken cancellationToken);
    }

    /// <summary>
    /// VM disk management: add, remove, and query hard drives.
    /// </summary>
    public interface IVmDiskManager
    {
        Task AddExistingHardDrive(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken);

        /// <summary>
        /// Attaches an existing VHDX choosing the controller: IDE is required
        /// for boot disks on Gen 1 VMs (BIOS firmware cannot boot SCSI).
        /// </summary>
        Task AddExistingHardDriveAsync(VmDeploymentPlan plan, string mediaPath, bool ide, CancellationToken cancellationToken);
        Task AddNewHardDrive(VmDeploymentPlan plan, string vmPath, CancellationToken cancellationToken);
        Task RemoveHardDrive(VmDeploymentPlan plan, int location, CancellationToken cancellationToken);
        Task<string[]> GetVmHardDiskPathsAsync(string vmName, CancellationToken cancellationToken);

        /// <summary>
        /// Every disk file the VM depends on: the attached disks, the disks
        /// of all its checkpoints, and each one's parent chain. With a
        /// checkpoint, <see cref="GetVmHardDiskPathsAsync"/> returns only the
        /// newest .avhdx — deleting just that strands the parent .vhdx.
        /// </summary>
        Task<string[]> GetVmDiskChainPathsAsync(string vmName, CancellationToken cancellationToken);

        /// <summary>
        /// Paths of the media currently inserted in the VM's DVD drives.
        /// </summary>
        Task<string[]> GetVmDvdMediaPathsAsync(string vmName, CancellationToken cancellationToken);
    }

    /// <summary>
    /// VM boot configuration: DVD drives and boot order.
    /// </summary>
    public interface IVmBootManager
    {
        Task AddBootDvd(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken);
        /// <summary>
        /// Attaches the ISO to a newly added DVD drive, leaving every existing drive
        /// untouched. Use for auxiliary media (unattend/cidata seeds) so the primary
        /// installer drive keeps its ISO.
        /// </summary>
        Task AttachIsoToNewDvdDrive(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken);
        /// <summary>
        /// Detaches media from the specific DVD drive that currently holds it,
        /// leaving any other DVD drives attached.
        /// </summary>
        Task RemoveBootDvd(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken);
        Task SetFirstBootToDvd(VmDeploymentPlan plan, CancellationToken cancellationToken);
        Task SetFirstBootToHardDrive(VmDeploymentPlan plan, CancellationToken cancellationToken);
        Task SetSecureBoot(VmDeploymentPlan plan, string secureBootTemplate, CancellationToken cancellationToken);
    }

    /// <summary>
    /// VM network adapter management.
    /// </summary>
    public interface IVmNetworkManager
    {
        Task AddNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken);
        Task ConnectNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken);
        Task AddTemporaryNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken);
        Task RemoveTemporaryNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken);

        /// <summary>
        /// True when a virtual switch with the given name exists on this host.
        /// Used by the CLI's pre-download validation so a mistyped --switch
        /// fails as InvalidArguments instead of after the VM skeleton exists.
        /// </summary>
        Task<bool> SwitchExistsAsync(string switchName, CancellationToken cancellationToken);

        /// <summary>
        /// The primary ("Network Adapter") NIC's MAC address as Hyper-V
        /// reports it (12 hex digits, no separators). Static-addressing seeds
        /// match the interface by this MAC so the address lands on exactly
        /// that NIC — never on a deploy-plumbing adapter. Throws when the
        /// adapter or its MAC is missing.
        /// </summary>
        Task<string> GetPrimaryAdapterMacAddressAsync(VmDeploymentPlan plan, CancellationToken cancellationToken);
    }

    /// <summary>
    /// VM hardware and feature configuration.
    /// </summary>
    public interface IVmConfigManager
    {
        Task SetCpuCount(VmDeploymentPlan plan, CancellationToken cancellationToken);
        Task DisableDynamicMemory(VmDeploymentPlan plan, CancellationToken cancellationToken);
        Task EnableGuestServices(VmDeploymentPlan plan, CancellationToken cancellationToken);
        Task EnableVirtualization(VmDeploymentPlan plan, CancellationToken cancellationToken);
        Task SetEnhancedSession(VmDeploymentPlan plan, CancellationToken cancellationToken);
        /// <summary>
        /// Stores the gallery item's published login for the VM in the
        /// encrypted metadata store shown by VMCreate Manager. Strategies call
        /// it only when those credentials actually apply to the deployed guest.
        /// </summary>
        Task RecordLoginCredentials(VmDeploymentPlan plan, string? initialUsername, string? initialPassword, CancellationToken cancellationToken);
        Task StartVMConnect(VmDeploymentPlan plan, CancellationToken cancellationToken);
    }
}
