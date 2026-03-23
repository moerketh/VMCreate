using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Removes the temporary NIC configuration (DHCP, firewall rules, systemd
    /// service) that was added during ISO customization for post-boot SSH access.
    /// Only acts when the cleanup script <c>/var/lib/vmcreate/restore_net.sh</c>
    /// exists — a no-op for guests that don't need temporary NIC setup.
    /// Runs at Order 830, before <see cref="DisableSshStep"/> (900).  The
    /// host-side temporary NIC is removed by the orchestrator in
    /// <see cref="HyperVVmCreator"/> after all post-boot steps complete.
    /// </summary>
    public class CleanupTemporaryNicStep : ICustomizationStep
    {
        public string Name => "Cleanup Temporary NIC";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public int Order => 830;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations) => true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            string check = await shell.RunCommandAsync(
                "test -f /var/lib/vmcreate/restore_net.sh && echo CLEANUP || echo SKIP", ct);

            if (check.Trim() == "CLEANUP")
            {
                logger.LogInformation("Running temporary NIC cleanup script on VM {VMName}", shell.VmName);
                // The script tears down eth1 (our SSH transport), so run it
                // in the background with a small delay to let SSH return first.
                await shell.RunCommandAsync(
                    "nohup bash -c 'sleep 2; sudo bash /var/lib/vmcreate/restore_net.sh' &>/dev/null &", ct);
            }
            else
            {
                logger.LogDebug("No temporary NIC cleanup needed on VM {VMName}", shell.VmName);
            }
        }
    }
}
