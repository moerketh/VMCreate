using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Removes VirtualBox Guest Additions from the guest VM.
    /// Handles three installation methods:
    ///   1. ISO-based: /opt/VBoxGuestAdditions-*/uninstall.sh
    ///   2. Package-based: virtualbox-guest-* deb/rpm packages
    ///   3. Leftover cleanup: kernel modules, mount points, services
    /// Safe no-op when VBox was never installed.
    /// <para>
    /// Chroot-capable: the script's only live-system actions (rmmod,
    /// systemctl disable/stop) are best-effort with swallowed failures —
    /// in the chroot they are no-ops (modules aren't loaded, no systemd),
    /// and first boot finds nothing left to remove or stop. Package
    /// removal and file cleanup work identically against the target tree.
    /// </para>
    /// </summary>
    public class RemoveVBoxGuestAdditionsStep : ICustomizationStep, IStepDisplayInfo
    {
        public string Name => "Remove VirtualBox Guest Additions";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 50; // Run early, before timezone/package steps
        public string? ProgressPhaseId => "Sub_RemoveVBox";
        public bool SupportsChroot => true;

        public string DeployTitle => "Remove VBox Guest Additions";
        public string DeployDescription => "Cleaning up VirtualBox artifacts";
        public string DeployIconName => "Delete24";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations) => true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Removing VirtualBox Guest Additions from VM {VMName}", shell.VmName);

            // Deploy the removal script, then execute it. This avoids all shell
            // quoting issues from nested bash -c layers in the SSH pipeline.
            // Normalize to LF — CopyContentAsync base64-encodes the string as-is,
            // and the C# verbatim literal contains Windows CRLF line endings.
            string script = ScriptResourceLoader.Load("remove_vbox.sh");
            await shell.CopyContentAsync(script, "/tmp/remove_vbox.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/remove_vbox.sh && sudo rm -f /tmp/remove_vbox.sh", ct);

            logger.LogInformation("VBox removal result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
