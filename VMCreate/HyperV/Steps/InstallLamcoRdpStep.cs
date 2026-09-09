using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Installs and configures the Lamco RDP Server (Wayland-native RDP) on the
    /// guest VM from a pinned fork release deb.
    /// <para>
    /// Unlike xrdp (which installs via the external cloning-ISO chroot), Lamco
    /// installs post-boot over SSH. It is a Wayland-native server built on IronRDP
    /// with PipeWire-based screen capture, so Wayland is kept
    /// enabled (the <see cref="ForceX11Step"/>/<see cref="DisableWaylandSessionsStep"/>
    /// steps are skipped via <see cref="VmCustomizations.RdpBackend"/> gating).
    /// </para>
    /// <para>
    /// The install path is exactly one: the pinned fork deb
    /// (tag + version + sha256 pinned in <c>Scripts/install_lamco.sh</c>) from
    /// <c>github.com/moerketh/lamco-rdp-server/releases</c>, sha256-verified
    /// before <c>dpkg -i</c>. There is no source-build fallback and no upstream
    /// fallback — a missing or re-pinned asset fails the deployment loudly
    /// rather than silently shipping a stock binary. After the install the
    /// script installs Portal/PipeWire runtime deps, generates TLS
    /// certificates, writes <c>/etc/lamco-rdp-server/config.toml</c>, installs
    /// the systemd <b>user</b> service units (including the automated one-time
    /// consent grant, <c>lamco-grant.service</c>), and enables linger.
    /// </para>
    /// <para>
    /// Gating: only runs when <see cref="VmCustomizations.RdpBackend"/> is
    /// <see cref="RdpBackend.Lamco"/> and the gallery item's
    /// <see cref="GalleryItem.LinuxDistro"/> is Debian-family
    /// (Ubuntu, Debian, Parrot) — the fork pipeline ships amd64 debs only.
    /// </para>
    /// <para>
    /// Runs at Order 235, before <see cref="EnableGraphicalAutologinStep"/> (238)
    /// and before the xrdp block (240-270, skipped for Lamco).
    /// </para>
    /// </summary>
    public class InstallLamcoRdpStep : ICustomizationStep
    {
        public string Name => "Install Lamco RDP Server";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 235;
        public string? ProgressPhaseId => "Sub_InstallLamcoRdp";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations.RdpBackend == RdpBackend.Lamco && item.SupportsLamco();

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Installing Lamco RDP Server on VM {VMName}", shell.VmName);

            string script = ScriptResourceLoader.Load("install_lamco.sh");

            // ONE autologin-user resolver, shared with EnableGraphicalAutologinStep
            // (order 238): item.InitialUsername is the authoritative source. The
            // two steps previously resolved the user independently (C# field vs
            // /etc/passwd guess) and a mismatch meant key.pem owned by the wrong
            // group and user units in the wrong home — a deployed-but-dead VM.
            // A blank field falls back to the script's /etc/passwd scan (first
            // non-system account with a home dir); the value is validated
            // before it ever reaches the root-run script.
            if (!string.IsNullOrWhiteSpace(item?.InitialUsername))
            {
                if (!UsernameValidator.IsValidLinuxUsername(item.InitialUsername))
                {
                    throw new InvalidOperationException(
                        $"Gallery item InitialUsername '{item.InitialUsername}' is not a valid Linux username. " +
                        "Refusing to substitute it into a root-run script.");
                }
                script = script.Replace("__AUTOLOGIN_USER__", item.InitialUsername);
            }
            else
            {
                logger.LogWarning("Gallery item has no InitialUsername for VM {VMName}; the install script will resolve the autologin user from /etc/passwd.", shell.VmName);
            }

            // Runtime distro re-verification: the gallery metadata is a HINT
            // (some loaders scrape mirror pages); the pinned fork deb is
            // Debian-family-only, so a mismatched hint must fail HERE rather
            // than inside the root-run script after packages are half-staged.
            var detected = await DistroDetector.DetectAsync(shell, ct);
            if (detected == LinuxDistro.Unknown || !detected.SupportsLamco())
            {
                throw new InvalidOperationException(
                    $"VM {shell.VmName} reports distro '{detected}' from /etc/os-release at runtime — not a Lamco-supported (Debian-family) distro. " +
                    "The gallery item's distro hint disagrees with the actual guest; aborting the Lamco install.");
            }

            // /tmp hardening: predictable root-run script paths in a
            // world-writable directory are a code-execution TOCTOU — an
            // attacker can pre-create /tmp/install_lamco.sh; `sudo tee`
            // writes INTO their file without taking ownership, and the
            // window between copy and execute lets the owner swap content.
            // An unpredictable path (host-generated GUID) makes
            // pre-creation infeasible; ownership+perms are tightened before
            // execution as defense in depth.
            string guestScript = $"/tmp/install_lamco_{Guid.NewGuid():N}.sh";
            await shell.CopyContentAsync(script, guestScript, ct);

            // The install pulls apt packages and downloads the fork deb —
            // beyond the transport's default command timeout, but well under
            // 20 minutes now that the on-VM Rust build fallback is gone.
            string result = await shell.RunCommandAsync(
                $"sudo chown root:root {guestScript} && sudo chmod 0700 {guestScript} && sudo bash {guestScript} && sudo rm -f {guestScript}",
                TimeSpan.FromMinutes(20), ct);

            // Result contract: the script prints a machine-readable terminal
            // line (LAMCO_RESULT=ok|degraded). Hard failures exit non-zero
            // before the line ever appears (RunCommandAsync throws), and a
            // missing line on a zero-exit run is treated as failed: a
            // completely swallowed install must not report success (same
            // class of bug as the HyperVVmCreator fabricated-success fix).
            var (outcome, detail) = ParseResultLine(result);
            switch (outcome)
            {
                case LamcoOutcome.Ok:
                    logger.LogInformation("Lamco RDP Server install result on VM {VMName}: {Result}", shell.VmName, result.Trim());
                    break;
                case LamcoOutcome.Degraded:
                    logger.LogWarning("Lamco RDP Server install completed DEGRADED on VM {VMName}. The deployment is usable but parts of the provisioning were skipped: {Result}", shell.VmName, detail);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Lamco RDP Server install on VM {shell.VmName} reported no LAMCO_RESULT line. " +
                        "The script exited zero but did not confirm success — treating as failure.");
            }
        }

        private static (LamcoOutcome outcome, string detail) ParseResultLine(string output)
        {
            // The terminal line is the LAST LAMCO_RESULT= in the output.
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("LAMCO_RESULT=", StringComparison.Ordinal))
                    last = trimmed;
            }
            if (last is null) return (LamcoOutcome.Failed, output.Trim());
            var value = last["LAMCO_RESULT=".Length..];
            if (value.Equals("ok", StringComparison.Ordinal))
                return (LamcoOutcome.Ok, string.Empty);
            if (value.Equals("degraded", StringComparison.Ordinal))
                return (LamcoOutcome.Degraded, output.Trim());
            return (LamcoOutcome.Failed, output.Trim());
        }

        private enum LamcoOutcome
        {
            Ok,
            Degraded,
            Failed
        }
    }
}

