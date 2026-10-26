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
        /// normally assigns the dynamic MAC at adapter-creation time, but a
        /// never-started VM skeleton can report the placeholder
        /// 000000000000 until first power-on (observed on a live host). The
        /// cloud-init seed's match-by-MAC rule needs a real address
        /// <em>pre-boot</em>, so in that case a deterministic
        /// locally-administered static MAC (02-…) is pinned onto the adapter
        /// first — 02-… stays clear of Hyper-V's 00-15-5D dynamic pool, and
        /// deriving it from the VM name keeps --replace re-deploys on the
        /// same address.
        /// </summary>
        public async Task<string> GetPrimaryAdapterMacAddressAsync(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            string? mac = await TryReadPrimaryMacAsync(plan, cancellationToken);

            if (string.IsNullOrWhiteSpace(mac) || mac == "000000000000")
            {
                string pinned = LocallyAdministeredMacFor(plan.VmName);
                var set = await _executor.RunCommandAsync("Set-VMNetworkAdapter",
                    new System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["VMName"] = plan.VmName,
                        ["Name"] = "Network Adapter",
                        ["StaticMacAddress"] = pinned,
                    }, cancellationToken);
                if (set.HadErrors)
                    throw new InvalidOperationException(
                        $"Primary NIC of VM '{plan.VmName}' has no assigned MAC, and pinning static MAC '{pinned}' failed: {set.ErrorSummary}");
                mac = await TryReadPrimaryMacAsync(plan, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(mac) || mac == "000000000000")
                throw new InvalidOperationException(
                    $"Could not read the MAC address of VM '{plan.VmName}'s primary network adapter. " +
                    "Static addressing matches the interface by MAC; without it the address could land on a " +
                    "deploy-plumbing NIC. Verify the adapter exists on the VM.");

            _logger.LogInformation("Primary adapter MAC for {VMName}: {Mac}", plan.VmName, mac);
            return mac;
        }

        private async Task<string?> TryReadPrimaryMacAsync(VmDeploymentPlan plan, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Get-VMNetworkAdapter",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["VMName"] = plan.VmName,
                    ["Name"] = "Network Adapter",
                    ["ErrorAction"] = "SilentlyContinue",
                }, cancellationToken);

            return result.Output.Count > 0
                ? result.Output[0].Properties["MacAddress"]?.Value?.ToString()
                : null;
        }

        /// <summary>Deterministic 02-XX-XX-XX-XX-XX (locally administered, unicast) MAC derived from the VM name.</summary>
        internal static string LocallyAdministeredMacFor(string vmName)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("vmcreate-primary-nic:" + vmName));
            byte[] mac = new byte[6];
            mac[0] = 0x02;
            Array.Copy(hash, 0, mac, 1, 5);
            return string.Join("-", mac.Select(b => b.ToString("X2")));
        }
    }
}
