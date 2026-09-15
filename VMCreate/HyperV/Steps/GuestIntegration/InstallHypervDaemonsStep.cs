using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Installs the hyperv-daemons package and regenerates initramfs on the
    /// guest VM.
    /// <para>
    /// hyperv-daemons provides VSS backup integration, KVP data exchange, and
    /// the fcopy daemon.  Regenerating initramfs ensures that module blacklist
    /// changes (e.g. removing blacklist-hyperv.conf) take effect.
    /// </para>
    /// <para>
    /// Safe no-op on distros that don't have hyperv-daemons in their repos.
    /// </para>
    /// <para>
    /// Runs at Order 280, after <see cref="DisableWaylandSessionsStep"/> (270)
    /// and before <see cref="SyncTimezoneStep"/> (100).
    /// </para>
    /// </summary>
    public class InstallHypervDaemonsStep : ICustomizationStep
    {
        public string Name => "Install Hyper-V Daemons";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 280;
        public string? ProgressPhaseId => "Sub_InstallHypervDaemons";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations) => true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Installing hyperv-daemons on VM {VMName}", shell.VmName);

            string script = ScriptResourceLoader.Load("install_hyperv_daemons.sh");
            await shell.CopyContentAsync(script, "/tmp/install_hyperv_daemons.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/install_hyperv_daemons.sh && sudo rm -f /tmp/install_hyperv_daemons.sh", ct);

            logger.LogInformation("hyperv-daemons install result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
