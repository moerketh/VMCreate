using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Removes guest tooling from other hypervisors (VirtualBox Guest
    /// Additions, VMware open-vm-tools) from the guest VM and blacklists
    /// VMware kernel modules. Their drivers conflict with Hyper-V's synthetic
    /// devices and can cause instability or crashes. Safe no-op when neither
    /// was ever installed.
    /// <para>
    /// Handles both tool sets in one pass: package removal
    /// (apt/dnf/yum/pacman/zypper), VirtualBox ISO uninstaller, DKMS
    /// entries, services, kernel-module unload, VMware module blacklist,
    /// leftover config cleanup, and — only when something was actually
    /// removed — one initramfs regeneration and GRUB update.
    /// </para>
    /// <para>
    /// Chroot-capable: the script's only live-system actions (rmmod,
    /// systemctl disable/stop) are best-effort with swallowed failures —
    /// in the chroot they are no-ops (modules aren't loaded, no systemd),
    /// and first boot finds nothing left to remove or stop. Package
    /// removal, the blacklist, and the initramfs/GRUB tools operate on
    /// the target tree's files, not the running kernel.
    /// </para>
    /// <para>
    /// Runs at Order 50, after <see cref="EnablePasswordlessSudoStep"/> (45,
    /// which bootstraps the passwordless sudo every later step relies on)
    /// and before the timezone/package steps (100+).
    /// </para>
    /// </summary>
    public class RemoveGuestToolsStep : ICustomizationStep, IStepDisplayInfo
    {
        public string Name => "Remove VirtualBox/VMware Tools";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 50; // Run early, before timezone/package steps
        public string? ProgressPhaseId => "Sub_RemoveGuestTools";
        public bool SupportsChroot => true;

        public string DeployTitle => "Remove VirtualBox/VMware Tools";
        public string DeployDescription => "Removing VirtualBox/VMware tools and blacklisting their drivers";
        public string DeployIconName => "Delete24";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations) => true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Removing VirtualBox/VMware guest tools and blacklisting foreign drivers on VM {VMName}", shell.VmName);

            // Deploy the removal script, then execute it. This avoids all shell
            // quoting issues from nested bash -c layers in the SSH pipeline.
            string script = ScriptResourceLoader.Load("remove_guest_tools.sh");
            await shell.CopyContentAsync(script, "/tmp/remove_guest_tools.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/remove_guest_tools.sh && sudo rm -f /tmp/remove_guest_tools.sh", ct);

            logger.LogInformation("Guest tools removal result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
