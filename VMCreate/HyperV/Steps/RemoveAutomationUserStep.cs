using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Removes the <c>vmcreate</c> automation user that was created during ISO
    /// customization for post-boot SSH access.  Because we are SSH'd in as
    /// this user — and later steps still need both SSH auth and sudo — we
    /// cannot delete anything immediately.  Instead we schedule a one-shot
    /// systemd service that on next boot: removes the sudoers entry, deletes
    /// the user + home, cleans up logs, and then self-disables.
    /// Runs at Order 850, after <see cref="CleanupTemporaryNicStep"/> (830)
    /// and before <see cref="DisableSshStep"/> (900).
    /// </summary>
    public class RemoveAutomationUserStep : ICustomizationStep
    {
        public string Name => "Remove Automation User";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public int Order => 850;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations) => true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            string check = await shell.RunCommandAsync(
                "id vmcreate >/dev/null 2>&1 && echo EXISTS || echo ABSENT", ct);

            if (check.Trim() != "EXISTS")
            {
                logger.LogDebug("No vmcreate user found on VM {VMName}", shell.VmName);
                return;
            }

            logger.LogInformation("Scheduling vmcreate automation user removal on VM {VMName}", shell.VmName);

            // Schedule full cleanup for next boot.  We cannot remove the
            // sudoers entry or authorized_keys now because subsequent
            // post-boot steps (DisableSshStep) still need SSH + sudo.
            await shell.RunCommandAsync(
                "sudo bash -c '" +
                "cat > /etc/systemd/system/vmcreate-cleanup.service <<EOF\n" +
                "[Unit]\n" +
                "Description=Remove vmcreate automation user\n" +
                "After=multi-user.target\n" +
                "[Service]\n" +
                "Type=oneshot\n" +
                "ExecStart=/bin/bash -c \"" +
                    "rm -f /etc/sudoers.d/vmcreate; " +
                    "rm -f /var/log/vmcreate-autorun.log; " +
                    "rm -f /var/log/vmcreate-temp-net.log; " +
                    "rm -rf /var/lib/vmcreate; " +
                    "userdel -r vmcreate 2>/dev/null; " +
                    "systemctl disable vmcreate-cleanup.service; " +
                    "rm -f /etc/systemd/system/vmcreate-cleanup.service\"\n" +
                "[Install]\n" +
                "WantedBy=multi-user.target\n" +
                "EOF\n" +
                "systemctl enable vmcreate-cleanup.service" +
                "'", ct);
        }
    }
}
