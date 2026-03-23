using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// VM lifecycle operations: create, start, stop, remove, and find VMs.
    /// </summary>
    public interface IVmLifecycleManager
    {
        Task CreateVMAsync(VmSettings vmSettings, string vmPath, int targetGeneration, CancellationToken cancellationToken);
        Task StartVM(VmSettings vmSettings, CancellationToken cancellationToken);
        Task<string[]> FindExistingVmsByBaseNameAsync(string baseName, CancellationToken cancellationToken);
        Task StopVMAsync(string vmName, CancellationToken cancellationToken);
        Task RemoveVMAsync(string vmName, CancellationToken cancellationToken);
    }

    /// <summary>
    /// VM disk management: add, remove, and query hard drives.
    /// </summary>
    public interface IVmDiskManager
    {
        Task AddExistingHardDrive(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken);
        Task AddNewHardDrive(VmSettings vmSettings, string vmPath, CancellationToken cancellationToken);
        Task RemoveHardDrive(VmSettings vmSettings, int location, CancellationToken cancellationToken);
        Task<string[]> GetVmHardDiskPathsAsync(string vmName, CancellationToken cancellationToken);
    }

    /// <summary>
    /// VM boot configuration: DVD drives and boot order.
    /// </summary>
    public interface IVmBootManager
    {
        Task AddBootDvd(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken);
        Task RemoveBootDvd(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken);
        Task SetFirstBootToDvd(VmSettings vmSettings, CancellationToken cancellationToken);
        Task SetFirstBootToHardDrive(VmSettings vmSettings, CancellationToken cancellationToken);
        Task SetSecureBoot(VmSettings vmSettings, CancellationToken cancellationToken);
    }

    /// <summary>
    /// VM network adapter management.
    /// </summary>
    public interface IVmNetworkManager
    {
        Task AddNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken);
        Task ConnectNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken);
        Task AddTemporaryNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken);
        Task RemoveTemporaryNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken);
    }

    /// <summary>
    /// VM hardware and feature configuration.
    /// </summary>
    public interface IVmConfigManager
    {
        Task SetCpuCount(VmSettings vmSettings, CancellationToken cancellationToken);
        Task DisableDynamicMemory(VmSettings vmSettings, CancellationToken cancellationToken);
        Task EnableGuestServices(VmSettings vmSettings, CancellationToken cancellationToken);
        Task EnableVirtualization(VmSettings vmSettings, CancellationToken cancellationToken);
        Task SetEnhancedSession(VmSettings vmSettings, CancellationToken cancellationToken);
        Task SetVMLoginNotes(VmSettings vmSettings, string initialUsername, string initialPassword, CancellationToken cancellationToken);
        Task StartVMConnect(VmSettings vmSettings, CancellationToken cancellationToken);
    }
}
