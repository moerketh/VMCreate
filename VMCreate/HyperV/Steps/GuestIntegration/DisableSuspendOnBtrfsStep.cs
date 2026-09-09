using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Disables suspend and hibernation on btrfs-root guest VMs by writing a
    /// systemd sleep.conf drop-in.
    /// <para>
    /// When a Hyper-V VM is suspended (e.g. overnight) and then resumed, the
    /// btrfs filesystem can stall while recovering from the freeze/thaw cycle.
    /// This causes <c>systemd-journald</c> worker threads to block in
    /// <c>fsync()</c>, triggering the watchdog to kill journald. The RDP
    /// transport (usock) then breaks, leaving active RDP sessions stale/dead
    /// even though the KDE desktop session is still running.
    /// </para>
    /// <para>
    /// Disabling suspend eliminates the root cause. On non-btrfs root
    /// filesystems (ext4, xfs, etc.) this step is a safe no-op — those
    /// filesystems handle post-resume I/O stalls gracefully.
    /// </para>
    /// <para>
    /// Runs at Order 200, after package install steps and before configuration
    /// steps such as <see cref="SyncTimezoneStep"/> (100).
    /// </para>
    /// </summary>
    public class DisableSuspendOnBtrfsStep : ICustomizationStep
    {
        public string Name => "Disable Suspend on btrfs";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 200;
        public string? ProgressPhaseId => "Sub_DisableSuspendOnBtrfs";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations) => true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Checking btrfs root filesystem on VM {VMName}", shell.VmName);

            string script = ScriptResourceLoader.Load("disable_suspend_btrfs.sh");
            await shell.CopyContentAsync(script, "/tmp/disable_suspend_btrfs.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/disable_suspend_btrfs.sh && sudo rm -f /tmp/disable_suspend_btrfs.sh", ct);

            logger.LogInformation("btrfs suspend-disable result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
