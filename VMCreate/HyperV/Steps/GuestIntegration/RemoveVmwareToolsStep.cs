using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Removes VMware open-vm-tools and blacklists VMware kernel modules from the
    /// guest VM post-boot.  VMware drivers conflict with Hyper-V's synthetic devices
    /// and can cause instability or crashes.  Safe no-op when VMware tools were never
    /// installed.
    /// <para>
    /// Handles: package removal (apt/dnf/pacman/zypper), kernel module blacklist,
    /// leftover config cleanup, initramfs regeneration, and GRUB update.
    /// </para>
    /// <para>
    /// Runs at Order 230, right after <see cref="RemoveVBoxGuestAdditionsStep"/> (220)
    /// and before <see cref="ForceX11Step"/> (240).
    /// </para>
    /// </summary>
    public class RemoveVmwareToolsStep : ICustomizationStep
    {
        public string Name => "Remove VMware Tools";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 230;
        public string? ProgressPhaseId => "Sub_RemoveVmwareTools";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations) => true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Removing VMware tools and blacklisting VMware drivers on VM {VMName}", shell.VmName);

            // Deploy the removal script, then execute it. This avoids all shell
            // quoting issues from nested bash -c layers in the SSH pipeline.
            // Normalize to LF -- CopyContentAsync base64-encodes the string as-is,
            // and the C# verbatim literal contains Windows CRLF line endings.
            string script = ScriptResourceLoader.Load("remove_vmware.sh");
            await shell.CopyContentAsync(script, "/tmp/remove_vmware.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/remove_vmware.sh && sudo rm -f /tmp/remove_vmware.sh", ct);

            logger.LogInformation("VMware removal result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
