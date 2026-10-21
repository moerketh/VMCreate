using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Deletes the temporary NOPASSWD sudoers drop-in installed by
    /// <see cref="EnablePasswordlessSudoStep"/> (order 45) so the guest is
    /// left with its stock passworded sudo policy once provisioning is done.
    /// Order 880 keeps the drop-in alive for every provisioning step and the
    /// automation-user teardown (850), while still running inside the SSH
    /// window before DisableSshStep (900).
    /// </summary>
    public class RemovePasswordlessSudoStep : ICustomizationStep, IStepDisplayInfo
    {
        public string Name => "Remove passwordless sudo drop-in";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 880;
        public string? ProgressPhaseId => null;

        public string DeployTitle => "Remove Passwordless Sudo";
        public string DeployDescription => "Removing the temporary NOPASSWD sudo drop-in";
        public string DeployIconName => "Key24";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => item != null && item.HasTag(EnablePasswordlessSudoStep.OmarchyTag);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // Best-effort: a leftover drop-in weakens sudo but never breaks
            // the deployment, and the user can remove it manually. No
            // password needed — the drop-in itself grants NOPASSWD sudo.
            string result = await shell.RunCommandAsync(
                $"sudo -n rm -f {EnablePasswordlessSudoStep.DropInPath} && echo REMOVED || echo LEFT", ct);

            if (result.Contains("REMOVED"))
                logger.LogInformation("Removed NOPASSWD sudo drop-in on VM {VMName} — stock passworded sudo policy restored.", shell.VmName);
            else
                logger.LogWarning("Could not remove the NOPASSWD sudo drop-in on VM {VMName}; remove {DropInPath} manually.", shell.VmName, EnablePasswordlessSudoStep.DropInPath);
        }
    }
}
