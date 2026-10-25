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
    /// PowerShell-backed implementation of <see cref="IVmLifecycleManager"/>.
    /// </summary>
    public class PowerShellVmLifecycleManager : IVmLifecycleManager
    {
        private readonly IPowerShellExecutor _executor;
        private readonly ILogger<PowerShellVmLifecycleManager> _logger;

        public PowerShellVmLifecycleManager(IPowerShellExecutor executor, ILogger<PowerShellVmLifecycleManager> logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task CreateVMAsync(VmDeploymentPlan plan, string vmPath, int targetGeneration, CancellationToken cancellationToken)
        {
            long memBytes = plan.MemoryInMB * 1024L * 1024L;
            _logger.LogInformation(
                "Invoking New-VM -Name '{Name}' -Path '{Path}' -Generation {Gen} -MemoryStartupBytes {Mem} -NoVHD",
                plan.VmName, vmPath, targetGeneration, memBytes);

            var result = await _executor.RunCommandAsync("New-VM",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["Name"] = plan.VmName,
                    ["MemoryStartupBytes"] = memBytes,
                    ["Path"] = vmPath,
                    ["Generation"] = targetGeneration,
                    ["NoVHD"] = true,
                }, cancellationToken);

            _logger.LogInformation("New-VM invoked for {VMName}: {ResultCount} result(s), HadErrors={HadErrors}",
                plan.VmName, result.Output.Count, result.HadErrors);

            if (result.HadErrors)
            {
                _logger.LogError("New-VM returned errors for {VMName}: {Errors}", plan.VmName, result.ErrorSummary);

                var existing = await _executor.RunCommandAsync("Get-VM",
                    new System.Collections.Generic.Dictionary<string, object?> { ["Name"] = plan.VmName },
                    cancellationToken);
                bool vmExists = existing.Output.Count > 0 && !existing.HadErrors;
                _logger.LogInformation("Post-New-VM existence check for {VMName}: exists={Exists}", plan.VmName, vmExists);

                if (!vmExists)
                    throw new Exception($"Failed to create a new virtual machine: {result.ErrorSummary}");

                _logger.LogWarning("VM {VMName} exists despite New-VM error stream — continuing with warning", plan.VmName);
            }

            await _executor.RunCommandAsync("Set-VM",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["Name"] = plan.VmName,
                    ["AutomaticCheckpointsEnabled"] = false,
                }, cancellationToken);
        }

        /// <summary>
        /// Starts the VM and fails fast with Hyper-V's own reason when it does
        /// not start. Start-VM reports failures such as "not enough memory"
        /// (0x8007000E) on the error stream rather than throwing; ignoring it
        /// left the deployment waiting minutes for a VM that never ran and
        /// then reporting only a generic start timeout.
        /// </summary>
        public async Task StartVM(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Start-VM",
                new System.Collections.Generic.Dictionary<string, object?> { ["Name"] = plan.VmName },
                cancellationToken);

            if (!result.HadErrors)
                return;

            _logger.LogError("Start-VM returned errors for {VMName}: {Errors}", plan.VmName, result.ErrorSummary);

            // An error alongside a running VM (a race with another start) is
            // not a failed start; only a VM that is not Running is.
            var state = await _executor.RunScriptAsync(
                $"(Get-VM -Name '{plan.VmName.Replace("'", "''")}').State.ToString()", cancellationToken);
            string? vmState = state.Output.FirstOrDefault()?.BaseObject?.ToString();
            if (string.Equals(vmState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("VM {VMName} is running despite the Start-VM error stream — continuing.", plan.VmName);
                return;
            }

            throw new Exception($"Failed to start virtual machine '{plan.VmName}' (state: {vmState ?? "unknown"}): {result.ErrorSummary}");
        }

        public async Task StopVMAsync(string vmName, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Stop-VM",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["Name"] = vmName,
                    ["Force"] = true,
                    ["TurnOff"] = true,
                }, cancellationToken);

            if (result.HadErrors)
            {
                _logger.LogWarning("Stop-VM had errors (VM may already be off): {Errors}", result.ErrorSummary);
            }
        }

        public async Task RemoveVMAsync(string vmName, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Remove-VM",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["Name"] = vmName,
                    ["Force"] = true,
                }, cancellationToken);

            if (result.HadErrors)
                throw new Exception($"Failed to remove VM {vmName}: {result.ErrorSummary}");

            _logger.LogInformation("Removed VM: {VMName}", vmName);
        }

        public async Task<string[]> FindExistingVmsByBaseNameAsync(string baseName, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Get-VM", null, cancellationToken);            return result.Output
                .Where(vm =>
                {
                    string name = vm.Properties["Name"]?.Value?.ToString() ?? "";
                    return string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(baseName + "_", StringComparison.OrdinalIgnoreCase);
                })
                .Select(vm => vm.Properties["Name"]?.Value?.ToString())
                .Where(n => n != null)
                .Select(n => n!)
                .ToArray();
        }
    }
}
