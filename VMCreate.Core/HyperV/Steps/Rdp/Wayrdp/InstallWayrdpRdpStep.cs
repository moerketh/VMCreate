using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Installs and configures wayrdp (MIT Wayland-native RDP server on
    /// FreeRDP 3) on the guest VM.
    /// <para>
    /// wayrdp is the replacement for the Lamco fork: same high-level shape
    /// (post-boot SSH install, Debian-family amd64 deb, systemd user units,
    /// kept-Wayland via PipeWire capture) but on our own protocol stack with
    /// compositor-specific backends — KDE first via the shared
    /// <c>hyperv-rdp-extras</c> crate (KWin virtual outputs + zkde capture).
    /// </para>
    /// <para>
    /// Status: Phase 0 — wayrdp has no installable artifact yet, so the
    /// embedded <c>install_wayrdp.sh</c> FAILS LOUDLY with a TODO marker.
    /// This step exists so the VMCreate E2E path (option selection → pipeline
    /// step → guest transport → result parsing) is exercised before the
    /// server is finished; the fail-fast contract is identical to
    /// <see cref="InstallLamcoRdpStep"/> — a silently absent RDP server is
    /// the worse failure. When the first deb ships, the script gains the
    /// same pinned-tag/sha256-verified contract and this step is unchanged.
    /// </para>
    /// <para>
    /// Gating: only when <see cref="VmCustomizations.RdpBackend"/> is
    /// <see cref="RdpBackend.Wayrdp"/> and the gallery item is Debian-family
    /// (<see cref="LinuxDistroExtensions.SupportsWayrdp(GalleryItem)"/>).
    /// The runtime distro re-verification (DistroDetector) is shared with
    /// the Lamco path: the gallery distro hint is only a hint; the pinned
    /// deb line is Debian-family-only, so a mismatched hint fails here
    /// rather than inside a root-run script.
    /// </para>
    /// <para>
    /// Not part of <see cref="RdpBackend.Auto"/> resolution — wayrdp is only
    /// reachable by explicit selection while it is pre-release.
    /// Runs at Order 237 (between the Lamco install at 235 and the
    /// <see cref="EnableGraphicalAutologinStep"/> at 238 — wayrdp, once it
    /// exists, needs its own user units installed before the session is
    /// brought up).
    /// </para>
    /// </summary>
    public class InstallWayrdpRdpStep : ICustomizationStep
    {
        public string Name => "Install wayrdp RDP Server";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 237;
        public string? ProgressPhaseId => "Sub_InstallWayrdpRdp";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => customizations?.RdpBackend == RdpBackend.Wayrdp && item.SupportsWayrdp();

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Installing wayrdp RDP Server on VM {VMName}", shell.VmName);

            string script = ScriptResourceLoader.Load("install_wayrdp.sh");

            // Same autologin-user contract as InstallLamcoRdpStep: the C#
            // side is the authoritative source; a blank field falls back to
            // the script's /etc/passwd scan. Replaced defensively even though
            // the placeholder script does not yet consume the token — the
            // real script will, and validation must already be in place.
            if (!string.IsNullOrWhiteSpace(item?.InitialUsername))
            {
                string initialUsername = item.InitialUsername;
                if (!UsernameValidator.IsValidLinuxUsername(initialUsername))
                {
                    throw new InvalidOperationException(
                        $"Gallery item InitialUsername '{initialUsername}' is not a valid Linux username. " +
                        "Refusing to substitute it into a root-run script.");
                }
                script = script.Replace("__AUTOLOGIN_USER__", initialUsername);
            }
            else
            {
                logger.LogWarning("Gallery item has no InitialUsername for VM {VMName}; the install script will resolve the autologin user from /etc/passwd.", shell.VmName);
            }

            // Runtime distro re-verification, identical to the Lamco path:
            // distinguish a verdict (Unknown = guest answered, not supported)
            // from no-verdict (null = SSH read failed; retry once).
            LinuxDistro? detected = null;
            for (int attempt = 1; ; attempt++)
            {
                detected = await DistroDetector.DetectAsync(shell, ct);
                if (detected is not null || attempt >= 2)
                    break;
                logger.LogWarning("Distro detection read failed on attempt {Attempt} for VM {VMName} (SSH transport error?) — retrying once.", attempt, shell.VmName);
            }

            if (detected == null)
            {
                throw new InvalidOperationException(
                    $"VM {shell.VmName}: could not read /etc/os-release over SSH after two attempts — ssh transport failure, not a distro verdict. " +
                    "Verify the guest is reachable (SSH up, automation user authorized) before re-running the wayrdp install.");
            }

            if (detected == LinuxDistro.Unknown || !detected.Value.SupportsWayrdp())
            {
                throw new InvalidOperationException(
                    $"VM {shell.VmName} reports distro '{detected.Value}' from /etc/os-release at runtime — not a wayrdp-supported (Debian-family) distro. " +
                    "The gallery item's distro hint disagrees with the actual guest; aborting the wayrdp install.");
            }

            // /tmp hardening: unpredictable guest path + tightened
            // ownership/perms before execution — see InstallLamcoRdpStep.
            string guestScript = $"/tmp/install_wayrdp_{Guid.NewGuid():N}.sh";
            await shell.CopyContentAsync(script, guestScript, ct);

            await shell.RunCommandAsync(
                $"sudo chown root:root {guestScript} && sudo chmod 0700 {guestScript} && sudo bash {guestScript} && sudo rm -f {guestScript}",
                TimeSpan.FromMinutes(20), ct);

            // NOTE: no WAYRDP_RESULT parsing yet — the placeholder script
            // always exits non-zero (RunCommandAsync throws), which is the
            // intended pre-release behavior. When the real install lands,
            // mirror ParseResultLine from InstallLamcoRdpStep with the
            // WAYRDP_RESULT=ok|degraded contract documented in the script.
        }
    }
}
