using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Restores the original SSH state if autorun temporarily enabled it.
    /// Only disables SSH if the marker file was left by autorun.sh, meaning
    /// the distro originally had SSH disabled (e.g. REMnux).
    /// Distros that ship with SSH enabled are left unchanged.
    /// </summary>
    public class DisableSshStep : ICustomizationStep
    {
        public string Name => "Restore SSH State";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public int Order => 900;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations) => true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            string check = await shell.RunCommandAsync(
                "test -f /var/lib/vmcreate/.ssh_was_disabled && echo RESTORE || echo KEEP", ct);

            if (check.Trim() == "RESTORE")
            {
                logger.LogInformation("SSH was originally disabled on VM {VMName}, restoring", shell.VmName);
                await shell.RunCommandAsync(
                    "sudo systemctl disable ssh.service sshd.service 2>/dev/null; " +
                    "sudo systemctl stop ssh.service sshd.service 2>/dev/null; " +
                    "sudo rm -f /var/lib/vmcreate/.ssh_was_disabled; " +
                    "echo 'SSH disabled for next boot'", ct);
            }
            else
            {
                logger.LogInformation("SSH was already enabled on VM {VMName}, leaving as-is", shell.VmName);
            }
        }
    }
}
