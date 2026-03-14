using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Removes VirtualBox Guest Additions from the guest VM post-boot.
    /// Runs the official uninstall.sh scripts found in /opt/VBoxGuestAdditions-*.
    /// Safe no-op when VBox was never installed.
    /// </summary>
    public class RemoveVBoxGuestAdditionsStep : ICustomizationStep
    {
        public string Name => "Remove VirtualBox Guest Additions";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public int Order => 50; // Run early, before timezone/package steps

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations) => true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Checking for VirtualBox Guest Additions on VM {VMName}", shell.VmName);

            string result = await shell.RunCommandAsync(
                "sudo bash -c 'found=0; for d in /opt/VBoxGuestAdditions-*; do " +
                "[ -x \"$d/uninstall.sh\" ] && echo \"Uninstalling $d\" && \"$d/uninstall.sh\" && found=1; " +
                "done; [ $found -eq 0 ] && echo \"No VirtualBox Guest Additions found\"'",
                ct);

            logger.LogInformation("VBox removal result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
