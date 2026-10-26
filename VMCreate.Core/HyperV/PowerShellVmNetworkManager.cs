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
            // plan.SwitchName selects the target switch (CLI --switch);
            // null keeps the stock Default Switch the deployment always used.
            string switchName = plan.SwitchName ?? "Default Switch";
            var result = await _executor.RunCommandAsync("Connect-VMNetworkAdapter",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["SwitchName"] = switchName,
                }, cancellationToken);
            if (result.HadErrors)
                throw new Exception($"Failed to connect network adapter: {result.ErrorSummary}");

            _logger.LogInformation("Connected VM to switch '{SwitchName}'.", switchName);
        }

        public async Task AddTemporaryNetworkAdapter(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            // The temporary adapter exists solely for host→guest SSH during
            // the ISO boot cycle. It always rides the Default Switch: it is
            // deploy plumbing, not part of the guest's final network, and
            // following plan.SwitchName here would pin post-boot SSH to a
            // switch whose address pool the guest may not even use.
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

        /// <summary>
        /// Get-VMSwitch by exact name: any output row means the switch exists.
        /// A name that matches nothing yields an empty result (SilentlyContinue),
        /// not an error — absence is the answer, not a failure.
        /// </summary>
        public async Task<bool> SwitchExistsAsync(string switchName, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Get-VMSwitch",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["Name"] = switchName,
                    ["ErrorAction"] = "SilentlyContinue",
                }, cancellationToken);
            return !result.HadErrors && result.Output.Count > 0;
        }

        /// <summary>
        /// The primary adapter's MAC from Get-VMNetworkAdapter. Hyper-V
        /// assigns the dynamic MAC when the adapter is created (New-VM), so
        /// the value exists from VM-skeleton time — before any seed is built.
        /// </summary>
        public async Task<string> GetPrimaryAdapterMacAddressAsync(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Get-VMNetworkAdapter",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["Name"] = "Network Adapter",
                    ["ErrorAction"] = "SilentlyContinue",
                }, cancellationToken);

            string? mac = result.Output.Count > 0
                ? result.Output[0].Properties["MacAddress"]?.Value?.ToString()
                : null;

            if (string.IsNullOrWhiteSpace(mac) || mac == "000000000000")
                throw new InvalidOperationException(
                    $"Could not read the MAC address of VM '{plan.VmName}'s primary network adapter. " +
                    "Static addressing matches the interface by MAC; without it the address could land on a " +
                    "deploy-plumbing NIC. Verify the adapter exists on the VM.");

            _logger.LogInformation("Primary adapter MAC for {VMName}: {Mac}", plan.VmName, mac);
            return mac;
        }
    }
}
