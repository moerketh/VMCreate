using CreateVM;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV;

namespace VMCreate
{
    /// <summary>
    /// PowerShell-backed implementation of <see cref="IVmDiskManager"/>.
    /// </summary>
    public class PowerShellVmDiskManager : IVmDiskManager
    {
        private readonly IPowerShellExecutor _executor;
        private readonly ILogger<PowerShellVmDiskManager> _logger;

        public PowerShellVmDiskManager(IPowerShellExecutor executor, ILogger<PowerShellVmDiskManager> logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        internal static string GetNewDrivePath(string vmPath, string vmName)
            => Path.Combine(vmPath, $"{vmName}_boot.vhdx");

        public async Task AddNewHardDrive(VmDeploymentPlan plan, string vmPath, CancellationToken cancellationToken)
        {
            string newVhdPath = GetNewDrivePath(vmPath, plan.VmName);

            var newVhd = await _executor.RunCommandAsync("New-VHD",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["Path"] = newVhdPath,
                    ["Dynamic"] = true,
                    ["SizeBytes"] = plan.NewDriveSizeInGB * 1024L * 1024L * 1024L,
                }, cancellationToken);
            if (newVhd.HadErrors)
                throw new Exception($"Failed to create new VHDX: {newVhd.ErrorSummary}");
            _logger.LogInformation("Created new dynamic VHDX for cloning: {Path}", newVhdPath);

            var attach = await _executor.RunCommandAsync("Add-VMHardDiskDrive",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["Path"] = newVhdPath,
                    ["ControllerType"] = "SCSI",
                }, cancellationToken);
            if (attach.HadErrors)
                throw new Exception($"Failed to attach new VHDX: {attach.ErrorSummary}");
            _logger.LogInformation("Attached new dynamic VHDX for cloning: {Path}", newVhdPath);
        }

        public async Task AddExistingHardDrive(VmDeploymentPlan plan, string mediaPath, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Add-VMHardDiskDrive",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["Path"] = mediaPath,
                    ["ControllerType"] = "SCSI",
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to attach VHDX {mediaPath}: {result.ErrorSummary}");
            _logger.LogInformation("Attached VHDX: {Path}", mediaPath);
        }

        public async Task RemoveHardDrive(VmDeploymentPlan plan, int location, CancellationToken cancellationToken)
        {
            var drives = await _executor.RunCommandAsync("Get-VMHardDiskDrive",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["ControllerType"] = "SCSI",
                    ["ControllerNumber"] = 0,
                    ["ControllerLocation"] = location,
                }, cancellationToken);

            if (drives.Output.Count == 0)
            {
                _logger.LogWarning("No hard drive found at SCSI(0,{Location}) for VM {VMName} — nothing to remove.", location, plan.VmName);
                return;
            }

            var result = await _executor.RunCommandAsync("Remove-VMHardDiskDrive",
                new System.Collections.Generic.Dictionary<string, object?> { ["VMHardDiskDrive"] = drives.Output[0] },
                cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to remove hard drive at SCSI(0,{location}): {result.ErrorSummary}");

            _logger.LogInformation("Detached disk at SCSI(0,{Location}) for VM {VMName}", location, plan.VmName);
        }

        public async Task<string[]> GetVmHardDiskPathsAsync(string vmName, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Get-VMHardDiskDrive",
                new System.Collections.Generic.Dictionary<string, object?> { ["VMName"] = vmName },
                cancellationToken);

            return result.Output
                .Select(d => d.Properties["Path"]?.Value?.ToString())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();
        }
    }
}
