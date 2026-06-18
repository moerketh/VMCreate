using CreateVM;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV;

namespace VMCreate
{
    /// <summary>
    /// PowerShell-backed implementation of <see cref="IVmNetworkManager"/>.
    /// </summary>
    public class PowerShellVmNetworkManager : IVmNetworkManager
    {
        private readonly IPowerShellExecutor _executor;
        private readonly ILogger<PowerShellVmNetworkManager> _logger;

        public PowerShellVmNetworkManager(IPowerShellExecutor executor, ILogger<PowerShellVmNetworkManager> logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task AddNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Add-VMNetworkAdapter",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["Name"] = "Network Adapter",
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to add network adapter: {result.ErrorSummary}");
        }

        public async Task ConnectNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Connect-VMNetworkAdapter",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["SwitchName"] = "Default Switch",
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to connect network adapter: {result.ErrorSummary}");

            _logger.LogInformation("Connected VM to Default Switch for internet access.");
        }

        public async Task AddTemporaryNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Add-VMNetworkAdapter",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["Name"] = "VMCreate Temp",
                    ["SwitchName"] = "Default Switch",
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to add temporary network adapter: {result.ErrorSummary}");

            _logger.LogInformation("Added temporary network adapter 'VMCreate Temp' on Default Switch.");
        }

        public async Task RemoveTemporaryNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            string escapedVmName = plan.VmName.Replace("'", "''");
            string script = $@"
                $adapter = Get-VMNetworkAdapter -VMName '{escapedVmName}' -Name 'VMCreate Temp' -ErrorAction SilentlyContinue
                if ($adapter) {{ Remove-VMNetworkAdapter -VMNetworkAdapter $adapter }}
            ";

            await _executor.RunScriptAsync(script, cancellationToken);
            _logger.LogInformation("Removed temporary network adapter 'VMCreate Temp' (if present).");
        }
    }
}
