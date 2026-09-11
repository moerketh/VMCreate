using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Forces the X11 display server on the guest VM by installing X11 runtime
    /// prerequisites (dbus-x11, Xwrapper.config), restoring previously disabled
    /// Wayland sessions, detecting available X11 sessions, and configuring SDDM,
    /// LightDM, and GDM to use X11.
    /// <para>
    /// Wayland on Hyper-V is unstable and can cause crashes or blank screens
    /// because the <c>hyperv_drm</c> driver (the synthetic graphics driver
    /// in Linux guests) has limited and incomplete atomic modesetting support.
    /// Wayland requires atomic modesetting for its rendering pipeline, so
    /// compositors that attempt to use it on Hyper-V may crash, freeze, or
    /// produce a blank display. X11 does not require atomic modesetting and
    /// works reliably with the <c>hyperv_drm</c> framebuffer.
    /// </para>
    /// <para>
    /// <b>Ordering is critical:</b> This step restores previously disabled
    /// Wayland session files first (so LightDM can resolve session names),
    /// then writes display manager configs that pin X11 as the default.
    /// Subsequent steps handle xrdp, KWin, AccountsService, Wayland disabling,
    /// and hyperv-daemons installation.
    /// </para>
    /// <para>
    /// This step is a safe no-op when no Wayland sessions exist.
    /// </para>
    /// <para>
    /// LightDM autologin is gated on <see cref="VmCustomizations.ConfigureXrdp"/>:
    /// when xRDP/Enhanced Session is enabled, autologin is omitted so display :0
    /// sits at the LightDM greeter and xRDP owns the user's session on :10,
    /// matching the autologin-disable in <c>install_xrdp.sh</c> and avoiding the
    /// dual-session D-Bus reboot hang. When xRDP is disabled, autologin is kept
    /// so the headless Hyper-V console does not hang at the greeter.
    /// </para>
    /// <para>
    /// Runs at Order 240, after <see cref="RemoveVmwareToolsStep"/> (230)
    /// and before <see cref="FixXrdpStep"/> (245).
    /// </para>
    /// </summary>
    public class ForceX11Step : ICustomizationStep
    {
        public string Name => "Force X11 Display Server";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 240;
        public string? ProgressPhaseId => "Sub_ForceX11";

        // Note the gate: != Lamco means BOTH Xrdp and None force X11/wayland-off.
        // That is deliberate but worth stating: "no RDP" is NOT "no desktop
        // changes" — the X11/wayland posture is still normalized so headless
        // Hyper-V consoles stay usable. If a future backend needs Wayland
        // without Lamco, this gate must become an explicit allowlist.
        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations?.RdpBackend != RdpBackend.Lamco;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Forcing X11 display server on VM {VMName}", shell.VmName);

            // Deploy the script, then execute it. This avoids all shell
            // quoting issues from nested bash -c layers in the SSH pipeline.
            // Normalize to LF -- CopyContentAsync base64-encodes the string as-is,
            // and the C# verbatim literal contains Windows CRLF line endings.
            //
            // Gate LightDM autologin on whether xRDP/Enhanced Session is enabled.
            // When ConfigureXrdp is true, the ISO installs xRDP (install_xrdp.sh)
            // which deliberately disables display-manager autologin to avoid a
            // dual-session D-Bus conflict: with autologin on, LightDM logs the
            // user into display :0 while xRDP opens a session on :10, and both
            // share one D-Bus user bus. Clicking Reboot from the xRDP desktop
            // then kills Plasma across both displays while systemd-logind
            // refuses to reboot because the LightDM session is still active --
            // black screen with cursor, reboot never happens. With autologin off,
            // :0 sits at the greeter and the xRDP session is the only active one.
            // When ConfigureXrdp is false, the Hyper-V console is the access
            // path, so keep autologin to avoid a hung greeter on a headless box.
            bool enableAutologin = !customizations.ConfigureXrdp;
            // Load the .sh resource (LF-pinned in git; the loader normalizes
            // as a safety net) and substitute the autologin gate placeholder.
            string script = ScriptResourceLoader.Load("force_x11.sh")
                .Replace("__ENABLE_AUTOLOGIN__", enableAutologin ? "1" : "0");
            await shell.CopyContentAsync(script, "/tmp/force_x11.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/force_x11.sh && sudo rm -f /tmp/force_x11.sh", ct);

            logger.LogInformation("X11 enforcement result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
