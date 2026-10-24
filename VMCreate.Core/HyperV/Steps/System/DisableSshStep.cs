using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Restores the original SSH state if the ISO stage temporarily enabled it.
    /// Only disables SSH if the marker file was left by the ISO stage's
    /// capture_ssh_state, meaning the distro originally had SSH disabled
    /// (e.g. REMnux). Distros that ship with SSH enabled are left unchanged.
    /// </summary>
    public class DisableSshStep : ICustomizationStep, IStepDisplayInfo
    {
        public string Name => "Restore SSH State";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 900;
        public string? ProgressPhaseId => "Sub_RestoreSsh";

        public string DeployTitle => "Restore SSH State";
        public string DeployDescription => "Restoring the original SSH configuration";
        public string DeployIconName => "ShieldKeyhole24";

        /// <summary>
        /// Skips headless appliances (tag "headless", e.g. Elastic SIEM):
        /// SSH is their only management access and is never disabled.
        /// </summary>
        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => !item.HasTag("headless");

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            // Debug preservation (debugger attached or VMCREATE_DEBUG_PRESERVE=1):
            // keep SSH up alongside the preserved vmcreate user (see
            // RemoveAutomationUserStep): a deployment preserved for
            // investigation must stay reachable.
            if (DebugPreservation.IsEnabled)
            {
                logger.LogWarning("Debug mode: skipping SSH disable on VM {VMName} — access preserved for investigation", shell.VmName);
                return;
            }

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
