using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Disables Wayland session files on the guest VM by moving them to a
    /// <c>disabled/</c> subdirectory, and unblocks Hyper-V kernel modules.
    /// <para>
    /// <b>Ordering is critical:</b> This step must run after display manager
    /// configs and AccountsService overrides are written, because LightDM
    /// and AccountsService read session names from .desktop files.  With
    /// <c>user-session</c> and <c>XSession</c> both pinned to X11 sessions,
    /// the cached Wayland name is irrelevant and disabling the file is safe.
    /// </para>
    /// <para>
    /// Also restores any previously disabled Wayland session files at the
    /// start, so that display managers can resolve session names during
    /// config-read before the files are disabled again.
    /// </para>
    /// <para>
    /// Safe no-op when no Wayland sessions exist.
    /// </para>
    /// <para>
    /// Runs at Order 270, after <see cref="FixAccountsServiceStep"/> (255)
    /// and before <see cref="InstallHypervDaemonsStep"/> (280).
    /// </para>
    /// </summary>
    public class DisableWaylandSessionsStep : ICustomizationStep, IStepDisplayInfo
    {
        public string Name => "Disable Wayland Sessions";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 270;
        public string? ProgressPhaseId => "Sub_DisableWaylandSessions";
        public bool SupportsChroot => true;

        public string DeployTitle => "Disable Wayland Sessions";
        public string DeployDescription => "Hiding Wayland sessions for xrdp compatibility";
        public string DeployIconName => "Prohibited24";

        // xrdp/X11-path only: hiding the Wayland .desktop files breaks
        // Wayland-native backends — for wayrdp (Hyprland), uwsm resolves its
        // launcher ("hyprland.desktop") by desktop id and sddm autologin then
        // dies on cold boot ("uwsm: Could not find entry"), leaving the VM at
        // the greeter with no Hyprland and no wayrdp/vsock listener for
        // vmconnect. Lamco/wayrdp keep the session files; None/Auto preserve
        // the historical default-path behavior.
        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => customizations?.RdpBackend is not RdpBackend.Lamco
                                      and not RdpBackend.Wayrdp;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Disabling Wayland sessions on VM {VMName}", shell.VmName);

            string script = ScriptResourceLoader.Load("disable_wayland.sh");
            await shell.CopyContentAsync(script, "/tmp/disable_wayland.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/disable_wayland.sh && sudo rm -f /tmp/disable_wayland.sh", ct);

            logger.LogInformation("Wayland disable result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
