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
    /// (post-boot SSH install, systemd USER unit, kept-Wayland headless
    /// capture) but on our own FreeRDP 3 protocol stack with
    /// compositor-specific backends — the wlroots backend drives Hyprland
    /// via <c>hyprctl output create headless</c> + ext-image-copy-capture.
    /// </para>
    /// <para>
    /// Package delivery: the wayrdp repo publishes public releases via CI,
    /// so the guest downloads the PINNED release asset itself — the script
    /// curls <c>releases/latest/download/wayrdp-&lt;ver&gt;-linux-x86_64.tar.gz</c>
    /// and verifies it against the sha256 pinned in
    /// <c>install_wayrdp.sh</c> before installing — the same
    /// pinned-URL-and-digest contract as <see cref="InstallLamcoRdpStep"/>'s
    /// fork-deb download, including the refuse-on-mismatch rule.
    /// </para>
    /// <para>
    /// Gating: only when <see cref="VmCustomizations.RdpBackend"/> is
    /// <see cref="RdpBackend.Wayrdp"/> and the gallery item is
    /// <see cref="LinuxDistroExtensions.SupportsWayrdp(GalleryItem)"/>
    /// (Debian-family or Arch-family — anywhere the distro packages ship
    /// FreeRDP 3, which wayrdp links at runtime). The runtime distro
    /// re-verification (DistroDetector) is shared with the Lamco path: the
    /// gallery distro hint is only a hint; a mismatched hint fails here
    /// rather than inside a root-run script.
    /// </para>
    /// <para>
    /// Not part of <see cref="RdpBackend.Auto"/> resolution — by design,
    /// wayrdp is an explicit opt-in selection rather than an automatic
    /// verdict.
    /// Runs at Order 237 (between the Lamco install at 235 and the
    /// <see cref="EnableGraphicalAutologinStep"/> at 238 — wayrdp's user
    /// unit is installed before the session is brought up). The result
    /// contract mirrors the Lamco step: the guest prints a terminal
    /// <c>WAYRDP_RESULT=ok|degraded</c> line and a zero-exit run missing
    /// that line is treated as failure.
    /// </para>
    /// </summary>
    public class InstallWayrdpRdpStep : ICustomizationStep, IStepDisplayInfo
    {
        public string Name => "Install wayrdp RDP Server";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 237;
        public string? ProgressPhaseId => "Sub_InstallWayrdpRdp";

        public string DeployTitle => "Install wayrdp RDP Server";
        public string DeployDescription => "Installing the Wayland-native RDP server (pinned release download, FreeRDP 3 libs, systemd user unit)";
        public string DeployIconName => "Desktop24";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => customizations?.RdpBackend == RdpBackend.Wayrdp && item.SupportsWayrdp();

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Installing wayrdp RDP Server on VM {VMName}", shell.VmName);

            string script = ScriptResourceLoader.Load("install_wayrdp.sh");

            // Package download is fully guest-side: the script curls the
            // pinned public release asset and verifies its sha256 against
            // the digest pinned in the script itself, so the host injects
            // no package data.

            // Same autologin-user contract as InstallLamcoRdpStep: the C#
            // side is the authoritative source; a blank field falls back to
            // the script's /etc/passwd scan.
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

            // NLA credential seed: wayrdp refuses to start without a usable
            // SAM (observed on a cold deploy: unit restart-loops with "no
            // usable NLA credentials"). Seed it from the gallery item's
            // initial password so first connect accepts the same creds as the
            // console. Base64 so arbitrary password bytes survive script
            // substitution; left as the placeholder when no password exists
            // (script then prints a loud NOTE and the readiness gate fails).
            if (!string.IsNullOrEmpty(item?.InitialPassword))
            {
                string pwB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(item.InitialPassword));
                script = script.Replace("__WAYRDP_NLA_PASSWORD_B64__", pwB64);
            }
            else
            {
                logger.LogWarning("Gallery item has no InitialPassword for VM {VMName}; the wayrdp NLA SAM will NOT be seeded — the unit will refuse to start until a SAM is provisioned manually.", shell.VmName);
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
                    $"VM {shell.VmName} reports distro '{detected.Value}' from /etc/os-release at runtime — not a wayrdp-supported (Debian-family or Arch-family) distro. " +
                    "The gallery item's distro hint disagrees with the actual guest; aborting the wayrdp install.");
            }

            // /tmp hardening: unpredictable guest path + tightened
            // ownership/perms before execution — see InstallLamcoRdpStep.
            string guestScript = $"/tmp/install_wayrdp_{Guid.NewGuid():N}.sh";
            await shell.CopyContentAsync(script, guestScript, ct);

            // The install pulls apt/pacman deps and downloads the pinned
            // release tarball — beyond the transport's default command
            // timeout, but well under 20 minutes.
            string result = await shell.RunCommandAsync(
                $"sudo chown root:root {guestScript} && sudo chmod 0700 {guestScript} && sudo bash {guestScript}; " +
                $"rc=$?; sudo rm -f {guestScript}; exit $rc",
                TimeSpan.FromMinutes(20), ct);

            // Result contract: the script prints a machine-readable terminal
            // line (WAYRDP_RESULT=ok|degraded), mirroring InstallLamcoRdpStep.
            // Hard failures exit non-zero before the line appears
            // (RunCommandAsync throws), and a missing line on a zero-exit run
            // is treated as failed: a completely swallowed install must not
            // report success.
            var (outcome, detail) = ParseResultLine(result);
            switch (outcome)
            {
                case WayrdpOutcome.Ok:
                    logger.LogInformation("wayrdp RDP Server install result on VM {VMName}: {Result}", shell.VmName, result.Trim());
                    break;
                case WayrdpOutcome.Degraded:
                    logger.LogWarning("wayrdp RDP Server install completed DEGRADED on VM {VMName}. The deployment is usable but parts of the provisioning were skipped: {Result}", shell.VmName, detail);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"wayrdp RDP Server install on VM {shell.VmName} reported no WAYRDP_RESULT line. " +
                        "The script exited zero but did not confirm success — treating as failure.");
            }
        }

        private static (WayrdpOutcome outcome, string detail) ParseResultLine(string output)
        {
            // The terminal line is the LAST WAYRDP_RESULT= in the output.
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("WAYRDP_RESULT=", StringComparison.Ordinal))
                    last = trimmed;
            }
            if (last is null) return (WayrdpOutcome.Failed, output.Trim());
            var value = last["WAYRDP_RESULT=".Length..];
            if (value.Equals("ok", StringComparison.Ordinal))
                return (WayrdpOutcome.Ok, string.Empty);
            if (value.Equals("degraded", StringComparison.Ordinal))
                return (WayrdpOutcome.Degraded, output.Trim());
            return (WayrdpOutcome.Failed, output.Trim());
        }

        private enum WayrdpOutcome
        {
            Ok,
            Degraded,
            Failed
        }
    }
}
