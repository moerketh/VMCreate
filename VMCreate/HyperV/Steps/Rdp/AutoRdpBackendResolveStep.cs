using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Resolves <see cref="RdpBackend.Auto"/> to a concrete backend at
    /// post-boot time, in the guest, by detecting the default display
    /// server — then mutates <see cref="VmCustomizations.RdpBackend"/> in
    /// place so every later step (and the deploy progress UI) sees the
    /// resolved choice.
    /// <para>
    /// Why runtime: the display server is a property of the booted desktop
    /// (GDM/SDDM defaults, wayland-sessions availability,
    /// x-session-manager alternative), which is neither reliably scrapeable
    /// from a download page nor stable across image releases. Pre-boot the
    /// answer is unknown — so under Auto nothing RDP-related is pre-installed
    /// via the cloning ISO (no VMCREATE_XRDP KVP;
    /// <see cref="VmCustomizations.ConfigureXrdp"/> reads false), and this
    /// step decides post-boot:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Wayland default + Debian-family distro</b> →
    /// <see cref="RdpBackend.Lamco"/> (Wayland-native; the right tool).</item>
    /// <item><b>Anything else</b> → <see cref="RdpBackend.Xrdp"/>, and
    /// <see cref="InstallXrdpPostBootStep"/> (order 236) backfills the
    /// install that the skipped chroot step would have done.</item>
    /// </list>
    /// <para>
    /// The step NEVER fails the deployment: a lost guest, a failed detection
    /// script, or an unreadable distro cannot be installed around, but they
    /// can be safely routed to xrdp (a conservative, always-installable
    /// fallback) with a loud warning in the log. The only rethrown exception
    /// is <see cref="OperationCanceledException"/>.
    /// </para>
    /// <para>
    /// Gating: only runs when <see cref="VmCustomizations.RdpBackend"/> is
    /// still <see cref="RdpBackend.Auto"/> (PostBootCustomizationService
    /// evaluates IsApplicable just-in-time per step).
    /// </para>
    /// </summary>
    public class AutoRdpBackendResolveStep : ICustomizationStep
    {
        public string Name => "Auto-select RDP backend";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 232;
        public string? ProgressPhaseId => "Sub_AutoRdpResolve";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations?.RdpBackend == RdpBackend.Auto;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation(
                "Auto-select RDP backend: detecting default display server on VM {VMName}...",
                shell.VmName);

            string? displayServer = null;

            // ── 1. Run the in-guest detection script ───────────────────────
            // Any failure (script exception, missing result line) leaves
            // displayServer null and falls through to the conservative x11
            // verdict below — detection is a routing decision, not a
            // deployment step, so it must not abort the deployment.
            try
            {
                string script = ScriptResourceLoader.Load("detect_display_server.sh");

                // /tmp hardening: unpredictable path + root-only perms
                // (same contract as every shipped script; the script itself
                // is read-only input, but keep the pattern uniform).
                string guestScript = $"/tmp/detect_display_server_{Guid.NewGuid():N}.sh";
                await shell.CopyContentAsync(script, guestScript, ct);

                string result = await shell.RunCommandAsync(
                    $"sudo chown root:root {guestScript} && sudo chmod 0700 {guestScript} && sudo bash {guestScript} && sudo rm -f {guestScript}",
                    TimeSpan.FromMinutes(2), ct);

                displayServer = ParseDetectLine(result);
                if (displayServer is null)
                {
                    logger.LogWarning(
                        "Auto-select RDP backend: detection script on VM {VMName} printed no RDP_DETECT line. Output: {Result}",
                        shell.VmName, TruncateForLog(result));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Auto-select RDP backend: display-server detection failed on VM {VMName}: {Message}. Falling back to xrdp.",
                    shell.VmName, ex.Message);
            }

            // ── 2. Confirm a Lamco-capable distro (runtime re-verification) ──
            // Lamco ships Debian-family debs only; the gallery hint is not
            // trusted. DetectAsync distinguishes a verdict (Unknown = guest
            // answered, not Debian-family) from no-verdict (null = SSH read
            // failed; one retry for a transport hiccup). A no-verdict guest
            // routes to xrdp with a loud warning — Lamco would have no
            // business on a guest we cannot even read.
            LinuxDistro? detected = null;
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    detected = await DistroDetector.DetectAsync(shell, ct);
                    if (detected is not null || attempt >= 2)
                        break;
                    logger.LogWarning(
                        "Distro detection read failed on attempt {Attempt} for VM {VMName} (SSH transport error?) — retrying once.",
                        attempt, shell.VmName);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Auto-select RDP backend: distro detection failed on VM {VMName}: {Message}. Falling back to xrdp.",
                    shell.VmName, ex.Message);
                detected = null;
            }

            if (detected is null)
            {
                logger.LogWarning(
                    "Auto-select RDP backend: /etc/os-release unreadable on VM {VMName} after two attempts — cannot confirm a Lamco-capable distro. Resolving to xrdp; InstallXrdpPostBootStep will install it.",
                    shell.VmName);
            }
            else if (detected == LinuxDistro.Unknown || !detected.Value.SupportsLamco())
            {
                logger.LogInformation(
                    "Auto-select RDP backend: VM {VMName} reports distro '{Distro}' — not Lamco-capable (Debian-family only). Lamco is excluded regardless of display server.",
                    shell.VmName, detected.Value);
            }

            bool lamcoCandidate = detected is not null
                && detected != LinuxDistro.Unknown
                && detected.Value.SupportsLamco();

            // ── 3. Verdict ────────────────────────────────────────────────
            RdpBackend resolved =
                displayServer == "wayland" && lamcoCandidate
                    ? RdpBackend.Lamco
                    : RdpBackend.Xrdp;

            // ── 4. Mutate the shared customizations IN PLACE ──────────────
            // The in-place mutation is the whole point: the deploy-progress
            // view model and every later step (InstallLamcoRdpStep 235,
            // InstallXrdpPostBootStep 236, the xrdp block 240+ gating on
            // !Lamco) read this same instance. The progress card for the
            // chosen branch is inserted when the next step reports in
            // (DeploymentProgressPresenter calls EnsureResolvedRdpBackendPhases
            // on every post-boot step report).
            customizations.RdpBackend = resolved;

            if (resolved == RdpBackend.Lamco)
            {
                logger.LogInformation(
                    "Auto-select RDP backend: VM {VMName} uses a Wayland session by default and runs {Distro} (Lamco-capable). Selected: LAMCO RDP SERVER.",
                    shell.VmName, detected!.Value);
            }
            else
            {
                string reason = displayServer == "x11"
                    ? "the desktop defaults to X11"
                    : "the default display server could not be confirmed";
                logger.LogInformation(
                    "Auto-select RDP backend: VM {VMName} — {Reason}. Selected: XRDP (InstallXrdpPostBootStep will backfill the install).",
                    shell.VmName, reason);
            }
        }

        /// <summary>
        /// Parses the terminal <c>RDP_DETECT=wayland|x11</c> line (last
        /// occurrence) from the detection script output. Returns null when
        /// the line is absent.
        /// </summary>
        private static string? ParseDetectLine(string output)
        {
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("RDP_DETECT=", StringComparison.Ordinal))
                {
                    var value = trimmed["RDP_DETECT=".Length..];
                    if (value.Equals("wayland", StringComparison.Ordinal)
                        || value.Equals("x11", StringComparison.Ordinal))
                    {
                        last = value;
                    }
                }
            }
            return last;
        }

        private static string TruncateForLog(string text)
        {
            const int Max = 500;
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return text.Length <= Max ? text : text[..Max] + "…";
        }
    }
}