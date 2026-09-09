using Microsoft.Extensions.Logging;
using System;
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
            => customizations.RdpBackend == RdpBackend.Lamco && item.SupportsLamco();

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            // A blank InitialUsername SKIPS the step (with a warning) rather
            // than configuring a root graphical autologin: GDM/SDDM mostly
            // refuse root autologin, and a passwordless root desktop is the
            // worst possible degradation for a gallery field we failed to
            // populate.
            if (string.IsNullOrWhiteSpace(item?.InitialUsername))
            {
                logger.LogWarning("Skipping graphical autologin on VM {VMName}: gallery item has no InitialUsername — no autologin user to configure.", shell.VmName);
                return;
            }

            var autologinUser = item.InitialUsername!;
            if (!UsernameValidator.IsValidLinuxUsername(autologinUser))
            {
                throw new InvalidOperationException(
                    $"Gallery item InitialUsername '{autologinUser}' is not a valid Linux username. " +
                    "Refusing to substitute it into a root-run script (the field can be sourced from distro mirror pages).");
            }

            logger.LogInformation("Enabling graphical Wayland autologin for user '{User}' on VM {VMName}", autologinUser, shell.VmName);

            string script = ScriptResourceLoader.Load("enable_autologin.sh")
                .Replace("__AUTOLOGIN_USER__", autologinUser);

            await shell.CopyContentAsync(script, "/tmp/enable_autologin.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/enable_autologin.sh && sudo rm -f /tmp/enable_autologin.sh", ct);

            // Result contract: AUTOLOGIN_RESULT=ok|degraded on the last line.
            // Hard failures exit non-zero (RunCommandAsync throws); a missing
            // line on a zero-exit run is treated as failed so a swallowed
            // install can never report success.
            var (outcome, detail) = ParseResultLine(result, "AUTOLOGIN_RESULT=");
            switch (outcome)
            {
                case AutologinOutcome.Ok:
                    logger.LogInformation("Graphical autologin result on VM {VMName}: {Result}", shell.VmName, result.Trim());
                    break;
                case AutologinOutcome.Degraded:
                    logger.LogWarning("Graphical autologin completed DEGRADED on VM {VMName}. The VM may boot to a greeter instead of the desktop: {Result}", shell.VmName, detail);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Graphical autologin on VM {shell.VmName} reported no AUTOLOGIN_RESULT line. " +
                        "The script exited zero but did not confirm success — treating as failure.");
            }
        }

        private static (AutologinOutcome outcome, string detail) ParseResultLine(string output, string marker)
        {
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(marker, StringComparison.Ordinal))
                    last = trimmed;
            }
            if (last is null) return (AutologinOutcome.Failed, output.Trim());
            var value = last[marker.Length..];
            if (value.Equals("ok", StringComparison.Ordinal))
                return (AutologinOutcome.Ok, string.Empty);
            if (value.Equals("degraded", StringComparison.Ordinal))
                return (AutologinOutcome.Degraded, output.Trim());
            return (AutologinOutcome.Failed, output.Trim());
        }

        private enum AutologinOutcome
        {
            Ok,
            Degraded,
            Failed
        }
    }
}


