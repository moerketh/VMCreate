using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Fixes xrdp configuration for Hyper-V by commenting out DRM options in
    /// xorg.conf and writing a startwm.sh that forces X11 with software rendering.
    /// <para>
    /// hyperv_drm does not expose a DRI render node, so xrdp sessions that load
    /// the xorgxrdp module will fail to start if DRMDevice, DRI3, or DRMAllowList
    /// lines are present.  The startwm.sh sets environment variables that force
    /// X11 rendering (KWIN_COMPOSE=N, QT_QUICK_BACKEND=software, etc.) and
    /// detects the appropriate desktop session at login time.
    /// </para>
    /// <para>
    /// Safe no-op when xrdp is not installed.
    /// </para>
    /// <para>
    /// Runs at Order 245, after <see cref="ForceX11Step"/> (240) and before
    /// <see cref="DisableKwinCompositingStep"/> (250).
    /// </para>
    /// </summary>
    public class FixXrdpStep : ICustomizationStep
    {
        public string Name => "Fix xrdp for Hyper-V";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 245;
        public string? ProgressPhaseId => "Sub_FixXrdp";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations?.RdpBackend != RdpBackend.Lamco;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Fixing xrdp configuration for Hyper-V on VM {VMName}", shell.VmName);

            string script = ScriptResourceLoader.Load("fix_xrdp.sh");
            await shell.CopyContentAsync(script, "/tmp/fix_xrdp.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/fix_xrdp.sh && sudo rm -f /tmp/fix_xrdp.sh", ct);

            logger.LogInformation("xrdp fix result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
