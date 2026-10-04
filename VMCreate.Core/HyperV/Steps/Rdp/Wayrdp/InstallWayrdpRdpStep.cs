using Microsoft.Extensions.Logging;
using System;
using System.IO;
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
    /// Package delivery (pre-public-release): the wayrdp repo is PRIVATE
    /// and has no public release asset, so the guest cannot download a
    /// package. The host instead scp's a locally-built tarball
    /// (<see cref="VmCustomizations.WayrdpPackagePath"/> — produced by
    /// <c>scripts/build-package.sh</c> in the wayrdp repo) to the guest
    /// alongside the script, computes its sha256 host-side and injects it
    /// into <c>install_wayrdp.sh</c> (<c>__WAYRDP_PKG_SHA256__</c>) so the
    /// guest verifies the digest before installing — same tamper-evident
    /// pin as <see cref="InstallLamcoRdpStep"/>, just host-delivered instead
    /// of network-fetched. When wayrdp ships a public release this becomes
    /// a pinned URL and the scp path is removed.
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
    /// Not part of <see cref="RdpBackend.Auto"/> resolution — wayrdp is only
    /// reachable by explicit selection while it is pre-release.
    /// Runs at Order 237 (between the Lamco install at 235 and the
    /// <see cref="EnableGraphicalAutologinStep"/> at 238 — wayrdp's user
    /// unit is installed before the session is brought up). The result
    /// contract mirrors the Lamco step: the guest prints a terminal
    /// <c>WAYRDP_RESULT=ok|degraded</c> line and a zero-exit run missing
    /// that line is treated as failure.
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

            // The wayrdp repo is private with no public release asset, so the
            // guest cannot fetch the package: the host delivers the locally-built
            // tarball over SSH instead. Resolve it from the customization, else
            // fail loudly before touching the guest (missing package is not a
            // retryable SSH error).
            string? pkgHost = customizations?.WayrdpPackagePath;
            if (string.IsNullOrWhiteSpace(pkgHost) || !File.Exists(pkgHost))
            {
                throw new InvalidOperationException(
                    $"VM {shell.VmName}: wayrdp package tarball not found. " +
                    $"Set VmCustomizations.WayrdpPackagePath to the output of scripts/build-package.sh " +
                    $"(dist/release/wayrdp-<ver>-linux-x86_64.tar.gz). Resolved path: '{pkgHost ?? "(unset)"}'.");
            }
            string pkgHostPath = pkgHost;

            // Compute the sha256 host-side and inject it so the guest verifies the
            // exact tarball it received (tamper-evident pin, mirroring the Lamco
            // sha256 contract but host-delivered instead of network-fetched).
            string pkgSha256;
            using (var stream = File.OpenRead(pkgHostPath))
                pkgSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            script = script.Replace("__WAYRDP_PKG_SHA256__", pkgSha256);

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

            // Deliver the package tarball + its sha256 sidecar to the guest
            // (unpredictable paths; the script expects them at these names).
            string guestTarball = "/tmp/wayrdp-pkg.tar.gz";
            string guestSha = "/tmp/wayrdp-pkg.sha256";
            await shell.CopyContentAsync(script, guestScript, ct);
            await shell.CopyFileAsync(pkgHostPath, guestTarball, ct);
            await shell.CopyContentAsync($"{pkgSha256}  wayrdp-pkg.tar.gz\n", guestSha, ct);

            string result = await shell.RunCommandAsync(
                $"sudo chown root:root {guestScript} && sudo chmod 0700 {guestScript} && sudo bash {guestScript}; " +
                $"rc=$?; sudo rm -f {guestScript} {guestTarball} {guestSha}; exit $rc",
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
