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
    }

    /// <summary>
    /// VM disk management: add, remove, and query hard drives.
    /// </summary>
    public interface IVmDiskManager
    {
        Task AddExistingHardDrive(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken);
        Task AddNewHardDrive(VmDeploymentPlan plan, string vmPath, CancellationToken cancellationToken);
        Task RemoveHardDrive(VmDeploymentPlan plan, int location, CancellationToken cancellationToken);
        Task<string[]> GetVmHardDiskPathsAsync(string vmName, CancellationToken cancellationToken);
    }

    /// <summary>
    /// VM boot configuration: DVD drives and boot order.
    /// </summary>
    public interface IVmBootManager
    {
        Task AddBootDvd(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken);
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
        Task SetVMLoginNotes(VmDeploymentPlan plan, string initialUsername, string initialPassword, CancellationToken cancellationToken);
        Task StartVMConnect(VmDeploymentPlan plan, CancellationToken cancellationToken);
    }
}
