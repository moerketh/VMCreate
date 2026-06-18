using CreateVM;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV;

namespace VMCreate
{
    /// <summary>
    /// PowerShell-backed implementation of <see cref="IVmBootManager"/>.
    /// </summary>
    public class PowerShellVmBootManager : IVmBootManager
    {
        private readonly IPowerShellExecutor _executor;
        private readonly ILogger<PowerShellVmBootManager> _logger;

        public PowerShellVmBootManager(IPowerShellExecutor executor, ILogger<PowerShellVmBootManager> logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task AddBootDvd(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Checking for DVD drive on VM: {VMName}", plan.VmName);

            var existing = await _executor.RunCommandAsync("Get-VMDvdDrive",
                new System.Collections.Generic.Dictionary<string, object?> { ["VMName"] = plan.VmName },
                cancellationToken);

            if (existing.Output.Count == 0)
            {
                var addResult = await _executor.RunCommandAsync("Add-VMDvdDrive",
                    new System.Collections.Generic.Dictionary<string, object?> { ["VMName"] = plan.VmName },
                    cancellationToken);
                if (addResult.HadErrors)
                    throw new Exception($"Failed to add DVD drive: {addResult.ErrorSummary}");
                _logger.LogInformation("Added DVD drive to VM: {VMName}", plan.VmName);
            }
            else
            {
                _logger.LogInformation("DVD drive already exists on VM: {VMName}", plan.VmName);
            }

            var setResult = await _executor.RunCommandAsync("Set-VMDvdDrive",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["Path"] = mediaPath,
                }, cancellationToken);
            if (setResult.HadErrors)
                throw new Exception($"Failed to attach ISO to DVD drive: {setResult.ErrorSummary}");

            _logger.LogInformation("Attached ISO to DVD drive: {MediaPath}", mediaPath);
        }

        public async Task RemoveBootDvd(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Set-VMDvdDrive",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["Path"] = null,
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to detach ISO from DVD drive: {result.ErrorSummary}");

            _logger.LogInformation("Detached ISO from DVD drive: {MediaPath}", mediaPath);
        }

        public async Task SetFirstBootToDvd(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var dvd = await _executor.RunCommandAsync("Get-VMDvdDrive",
                new System.Collections.Generic.Dictionary<string, object?> { ["VMName"] = plan.VmName },
                cancellationToken);
            var dvdDrive = dvd.Output.FirstOrDefault();
            if (dvdDrive == null)
                throw new Exception("No DVD drive found for VM. Ensure the cloning ISO is attached.");

            var result = await _executor.RunCommandAsync("Set-VMFirmware",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["FirstBootDevice"] = dvdDrive,
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to set DVD as first boot device: {result.ErrorSummary}");
        }

        public async Task SetFirstBootToHardDrive(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var drives = await _executor.RunCommandAsync("Get-VMHardDiskDrive",
                new System.Collections.Generic.Dictionary<string, object?> { ["VMName"] = plan.VmName },
                cancellationToken);
            var firstDrive = drives.Output.FirstOrDefault();
            if (firstDrive == null)
                throw new Exception("No hard disk drive found for VM. Ensure a VHDX is attached.");

            var result = await _executor.RunCommandAsync("Set-VMFirmware",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["FirstBootDevice"] = firstDrive,
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to set hard drive as first boot device: {result.ErrorSummary}");

            _logger.LogInformation("Set first boot device to hard drive for VM: {VMName}", plan.VmName);
        }

        public async Task SetSecureBoot(VmDeploymentPlan plan, string secureBootTemplate, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Set-VMFirmware",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["EnableSecureBoot"] = plan.SecureBoot.ToOnOff(),
                    ["SecureBootTemplate"] = secureBootTemplate ?? plan.SecureBootTemplate,
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to set secure boot: {result.ErrorSummary}");
        }
    }
}
