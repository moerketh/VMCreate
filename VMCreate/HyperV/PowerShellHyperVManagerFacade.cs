using CreateVM;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Backward-compatibility façade that exposes all Hyper-V role interfaces through
    /// <see cref="IHyperVManager"/>. All work is delegated to the focused role managers.
    /// New code should depend on the narrower role interfaces instead.
    /// </summary>
    internal sealed class PowerShellHyperVManagerFacade : IHyperVManager
    {
        private readonly IVmLifecycleManager _lifecycle;
        private readonly IVmDiskManager _disk;
        private readonly IVmBootManager _boot;
        private readonly IVmNetworkManager _network;
        private readonly IVmConfigManager _config;

        public PowerShellHyperVManagerFacade(
            IVmLifecycleManager lifecycle,
            IVmDiskManager disk,
            IVmBootManager boot,
            IVmNetworkManager network,
            IVmConfigManager config)
        {
            _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
            _disk = disk ?? throw new ArgumentNullException(nameof(disk));
            _boot = boot ?? throw new ArgumentNullException(nameof(boot));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public Task CreateVMAsync(VmDeploymentPlan plan, string vmPath, int targetGeneration, CancellationToken cancellationToken)
            => _lifecycle.CreateVMAsync(plan, vmPath, targetGeneration, cancellationToken);

        public Task StartVM(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _lifecycle.StartVM(plan, cancellationToken);

        public Task<string[]> FindExistingVmsByBaseNameAsync(string baseName, CancellationToken cancellationToken)
            => _lifecycle.FindExistingVmsByBaseNameAsync(baseName, cancellationToken);

        public Task StopVMAsync(string vmName, CancellationToken cancellationToken)
            => _lifecycle.StopVMAsync(vmName, cancellationToken);

        public Task RemoveVMAsync(string vmName, CancellationToken cancellationToken)
            => _lifecycle.RemoveVMAsync(vmName, cancellationToken);

        public Task AddExistingHardDrive(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken)
            => _disk.AddExistingHardDrive(plan, mediaPath, cancellationToken);

        public Task AddNewHardDrive(VmDeploymentPlan plan, string vmPath, CancellationToken cancellationToken)
            => _disk.AddNewHardDrive(plan, vmPath, cancellationToken);

        public Task RemoveHardDrive(VmDeploymentPlan plan, int location, CancellationToken cancellationToken)
            => _disk.RemoveHardDrive(plan, location, cancellationToken);

        public Task<string[]> GetVmHardDiskPathsAsync(string vmName, CancellationToken cancellationToken)
            => _disk.GetVmHardDiskPathsAsync(vmName, cancellationToken);

        public Task AddBootDvd(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken)
            => _boot.AddBootDvd(plan, mediaPath, cancellationToken);

        public Task RemoveBootDvd(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken)
            => _boot.RemoveBootDvd(plan, mediaPath, cancellationToken);

        public Task SetFirstBootToDvd(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _boot.SetFirstBootToDvd(plan, cancellationToken);

        public Task SetFirstBootToHardDrive(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _boot.SetFirstBootToHardDrive(plan, cancellationToken);

        public Task SetSecureBoot(VmDeploymentPlan plan, string secureBootTemplate, CancellationToken cancellationToken)
            => _boot.SetSecureBoot(plan, secureBootTemplate, cancellationToken);

        public Task AddNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _network.AddNetworkAdapter(plan, cancellationToken);

        public Task ConnectNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _network.ConnectNetworkAdapter(plan, cancellationToken);

        public Task AddTemporaryNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _network.AddTemporaryNetworkAdapter(plan, cancellationToken);

        public Task RemoveTemporaryNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _network.RemoveTemporaryNetworkAdapter(plan, cancellationToken);

        public Task SetCpuCount(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _config.SetCpuCount(plan, cancellationToken);

        public Task DisableDynamicMemory(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _config.DisableDynamicMemory(plan, cancellationToken);

        public Task EnableGuestServices(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _config.EnableGuestServices(plan, cancellationToken);

        public Task EnableVirtualization(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _config.EnableVirtualization(plan, cancellationToken);

        public Task SetEnhancedSession(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _config.SetEnhancedSession(plan, cancellationToken);

        public Task SetVMLoginNotes(VmDeploymentPlan plan, string initialUsername, string initialPassword, CancellationToken cancellationToken)
            => _config.SetVMLoginNotes(plan, initialUsername, initialPassword, cancellationToken);

        public Task StartVMConnect(VmDeploymentPlan plan, CancellationToken cancellationToken)
            => _config.StartVMConnect(plan, cancellationToken);
    }
}
