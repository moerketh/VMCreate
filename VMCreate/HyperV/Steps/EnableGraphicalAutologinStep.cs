using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Enables graphical autologin on the guest VM while keeping Wayland enabled,
    /// so the Lamco RDP Server (which shares an existing Wayland session) has a
    /// live desktop session to attach to at boot.
    /// <para>
    /// This is the Lamco counterpart to the autologin handling in
    /// <see cref="ForceX11Step"/>: when xrdp is selected, autologin is deliberately
    /// disabled so display :0 sits at the greeter and xrdp owns the session on
    /// :10 (avoiding a dual-session D-Bus reboot hang). Lamco, by contrast, shares
    /// the user's own Wayland session via XDG Desktop Portal + PipeWire, so it
    /// needs the user logged in at the console — hence autologin ON.
    /// </para>
    /// <para>
    /// Detects the active display manager at runtime (GDM, SDDM, or LightDM) and
    /// configures autologin for the VM's initial user
    /// (<see cref="GalleryItem.InitialUsername"/>), selecting a Wayland session
    /// from <c>/usr/share/wayland-sessions/*.desktop</c>. Does not touch
    /// <c>WaylandEnable</c> (Wayland stays on, unlike the xrdp path).
    /// </para>
    /// <para>
    /// Also adds the user to the <c>render</c> and <c>video</c> groups so PipeWire
    /// and VA-API hardware encoding can access GPU devices, and enables
    /// <c>loginctl enable-linger</c> so the user's systemd services (including the
    /// Lamco user unit) start at boot without an interactive SSH login.
    /// </para>
    /// <para>
    /// Runs at Order 238, after <see cref="InstallLamcoRdpStep"/> (235) and before
    /// the xrdp block (240-270, which is skipped for Lamco). Safe no-op when no
    /// display manager or Wayland session is present.
    /// </para>
    /// </summary>
    public class EnableGraphicalAutologinStep : ICustomizationStep
    {
        public string Name => "Enable Graphical Autologin (Wayland)";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 238;
        public string? ProgressPhaseId => "Sub_EnableAutologin";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations.RdpBackend == RdpBackend.Lamco;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            var autologinUser = !string.IsNullOrWhiteSpace(item?.InitialUsername) ? item.InitialUsername : "root";
            logger.LogInformation("Enabling graphical Wayland autologin for user '{User}' on VM {VMName}", autologinUser, shell.VmName);

            string script = ScriptResourceLoader.Load("enable_autologin.sh")
                .Replace("__AUTOLOGIN_USER__", autologinUser);

            await shell.CopyContentAsync(script, "/tmp/enable_autologin.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/enable_autologin.sh && sudo rm -f /tmp/enable_autologin.sh", ct);

            logger.LogInformation("Graphical autologin result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}

