using CreateVM;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV;

namespace VMCreate
{
    /// <summary>
    /// PowerShell-backed implementation of <see cref="IVmConfigManager"/>.
    /// </summary>
    public class PowerShellVmConfigManager : IVmConfigManager
    {
        private readonly IPowerShellExecutor _executor;
        private readonly ILogger<PowerShellVmConfigManager> _logger;

        public PowerShellVmConfigManager(IPowerShellExecutor executor, ILogger<PowerShellVmConfigManager> logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task SetCpuCount(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Set-VMProcessor",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["Count"] = plan.CpuCount,
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to set CPU count: {result.ErrorSummary}");
        }

        public async Task DisableDynamicMemory(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Set-VMMemory",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["DynamicMemoryEnabled"] = false,
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to disable dynamic memory: {result.ErrorSummary}");
        }

        public async Task EnableGuestServices(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Enable-VMIntegrationService",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["Name"] = "Guest Service Interface",
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to enable guest services: {result.ErrorSummary}");

            _logger.LogInformation("Enabled Guest services for VM: {VMName}", plan.VmName);
        }

        public async Task EnableVirtualization(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Set-VMProcessor",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["ExposeVirtualizationExtensions"] = true,
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to enable virtualization extensions: {result.ErrorSummary}");

            _logger.LogInformation("Enabled virtualization extensions for VM: {VMName}", plan.VmName);
        }

        public async Task SetEnhancedSession(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            string transportType = string.IsNullOrEmpty(plan.EnhancedSessionTransportType)
                ? "HvSocket"
                : plan.EnhancedSessionTransportType;

            var result = await _executor.RunCommandAsync("Set-VM",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["EnhancedSessionTransportType"] = transportType,
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to set enhanced session transport: {result.ErrorSummary}");
        }

        public async Task SetVMLoginNotes(VmDeploymentPlan plan, string initialUsername, string initialPassword, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Set-VM",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["Name"] = plan.VmName,
                    ["Notes"] = $"Initial Username: {initialUsername}\r\nInitial Password: {initialPassword}",
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to set VM login notes: {result.ErrorSummary}");
        }

        public async Task StartVMConnect(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            string escapedVmName = plan.VmName.Replace("'", "''");
            string vmConnectCommand = $"& \"C:\\Windows\\System32\\vmconnect.exe\" localhost \"{escapedVmName}\"";
            _logger.LogDebug("Executing VMConnect command: {Command}", vmConnectCommand);

            var result = await _executor.RunScriptAsync(vmConnectCommand, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to launch VMConnect: {result.ErrorSummary}");

            _logger.LogInformation("Successfully launched VMConnect for VM: {VMName}", plan.VmName);
        }
    }
}
