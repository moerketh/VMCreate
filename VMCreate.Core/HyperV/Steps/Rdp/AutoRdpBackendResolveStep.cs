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
    /// <item><b>Wayland default + Lamco-capable distro</b> (Debian-family
    /// deb or Arch-family pacman package) →
    /// <see cref="RdpBackend.Lamco"/> (Wayland-native; the right tool) —
    /// except <b>KDE Plasma 6.3+ on Kali or Parrot</b> (the detection script's
    /// <c>RDP_PLASMA</c> line), which resolves to <see cref="RdpBackend.Wayrdp"/>:
    /// the project's own server, validated there. Applies to a switched
    /// desktop as well (Kali → KDE).</item>
    /// <item><b>Nix-family distro</b> (NixOS / Athena Nix — detected at
    /// runtime) → <see cref="RdpBackend.None"/>: no installable backend
    /// exists there (post-boot installs through a package manager are
    /// structurally impossible on Nix; the fork pipeline ships no nix
    /// package), so xrdp is NOT a fallback for these guests. The deployment
    /// deliberately ends without RDP (console/SSH only) with a loud
    /// warning, and <see cref="InstallXrdpPostBootStep"/> is gated off
    /// NixOs items for the same reason.</item>
    /// <item><b>Switched to a Wayland desktop</b>
    /// (<see cref="VmCustomizations.SwitchedDesktopDisplayServer"/>, set by
    /// <see cref="DesktopSwitchStep"/>) → <see cref="RdpBackend.Lamco"/> on a
    /// Lamco-capable distro, else <see cref="RdpBackend.None"/> with a
    /// warning. Never xrdp, whatever detection reports.</item>
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
    /// still <see cref="RdpBackend.Auto"/> (the executor evaluates
    /// IsApplicable just-in-time per step).
    /// </para>
    /// </summary>
    public class AutoRdpBackendResolveStep : ICustomizationStep, IStepDisplayInfo
    {
        public string Name => "Auto-select RDP backend";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 232;
        public string? ProgressPhaseId => "Sub_AutoRdpResolve";
        public bool SupportsChroot => true;

        public string DeployTitle => "Auto-select RDP backend";
        public string DeployDescription => "Detecting Wayland vs X11 in the guest to choose the RDP backend";
        public string DeployIconName => "ArrowSync24";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            // Headless appliances (tag "headless", e.g. Elastic SIEM) have no
            // desktop to share — the whole RDP pipeline is skipped.
            //
            // A pending desktop switch makes the resolver inapplicable until
            // the switch completes: any display-server verdict produced now
            // would read the STOCK desktop (e.g. Kali's Xfce/X11) while the
            // backend must match the SWITCHED desktop (KDE/GNOME, Wayland).
            // DesktopSwitchStep (order 231) clears DesktopEnvironmentId when
            // it completes, and both executors re-evaluate this gate
            // just-in-time — so the resolver runs at 232 right after the
            // switch, in the same context (the ISO chroot, or post-boot in
            // the cloud-init flow), never before it: a verdict before the
            // switch would read the stock desktop and stick.
            => !item.HasTag("headless")
               && customizations?.RdpBackend == RdpBackend.Auto
               && !customizations.AutoRdpAwaitsDesktopSwitch;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation(
                "Auto-select RDP backend: detecting default display server on VM {VMName}...",
                shell.VmName);

            string? displayServer = null;
            (int Major, int Minor)? plasma = null;

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
                plasma = ParsePlasmaLine(result);
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
            // Lamco support is gated on the fork pipeline's pinned Debian
            // deb / Arch pacman package; the gallery hint is not
            // trusted. DetectAsync distinguishes a verdict (Unknown = guest
            // answered, not Lamco-capable) from no-verdict (null = SSH read
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
                    "Auto-select RDP backend: VM {VMName} reports distro '{Distro}' — not Lamco-capable (no pinned fork package for this lineage). Lamco is excluded regardless of display server.",
                    shell.VmName, detected.Value);
            }

            bool lamcoCandidate = detected is not null
                && detected != LinuxDistro.Unknown
                && detected.Value.SupportsLamco();

            // ── 3. Verdict ────────────────────────────────────────────────
            // A switch to a Wayland desktop (KDE, GNOME) never lands on xrdp:
            // xrdp there means forcing the desktop onto X11, and GNOME 49+
            // has no X11 session at all. Lamco when the distro can take it —
            // the gallery hint stands in when the guest could not be read —
            // otherwise no RDP backend rather than a broken one.
            bool switchedToWayland = customizations.SwitchedDesktopDisplayServer is { } switched
                && switched != DesktopDisplayServer.X11Only;

            RdpBackend resolved;
            if (detected == LinuxDistro.NixOs)
            {
                resolved = RdpBackend.None;
            }
            else if (switchedToWayland)
            {
                resolved = lamcoCandidate || (detected is null && item.SupportsLamco())
                    ? RdpBackend.Lamco
                    : RdpBackend.None;
            }
            else
            {
                resolved = displayServer == "wayland" && lamcoCandidate
                    ? RdpBackend.Lamco
                    : RdpBackend.Xrdp;
            }

            // KDE Plasma 6.3+ on Kali / Parrot: wayrdp instead of Lamco. Only
            // a detected distro counts (not the gallery hint), and only a
            // Plasma the detection script actually found: wayrdp refuses an
            // older KWin at startup, which would leave the VM without RDP.
            bool wayrdpKde = resolved == RdpBackend.Lamco
                && detected is { } d && d.SupportsWayrdpKde()
                && plasma is { } p && p.CompareTo(WayrdpMinPlasma) >= 0;
            if (wayrdpKde)
                resolved = RdpBackend.Wayrdp;

            // ── 4. Mutate the shared customizations IN PLACE ──────────────
            // The in-place mutation is the whole point: the deploy-progress
            // view model and every later step (InstallLamcoRdpStep 235,
            // InstallXrdpPostBootStep 236, the xrdp block 240+ gating on
            // !Lamco) read this same instance. The progress card for the
            // chosen branch is inserted when the next step reports in
            // (DeploymentProgressPresenter calls EnsureResolvedRdpBackendPhases
            // on every post-boot step report).
            customizations.RdpBackend = resolved;

            if (wayrdpKde)
            {
                logger.LogInformation(
                    "Auto-select RDP backend: VM {VMName} runs KDE Plasma {Major}.{Minor} on {Distro}. Selected: WAYRDP (KDE backend).",
                    shell.VmName, plasma!.Value.Major, plasma.Value.Minor, detected!.Value);
            }
            else if (switchedToWayland)
            {
                if (resolved == RdpBackend.Lamco && displayServer != "wayland")
                    logger.LogWarning(
                        "Auto-select RDP backend: VM {VMName} was switched to a Wayland desktop, but detection reported '{DisplayServer}'. Selected: LAMCO RDP SERVER anyway — xrdp cannot serve a Wayland desktop.",
                        shell.VmName, displayServer ?? "nothing");
                else if (resolved == RdpBackend.Lamco)
                    logger.LogInformation(
                        "Auto-select RDP backend: VM {VMName} was switched to a Wayland desktop. Selected: LAMCO RDP SERVER.",
                        shell.VmName);
                else
                    logger.LogWarning(
                        "Auto-select RDP backend: VM {VMName} was switched to a Wayland desktop, but the distro ('{Distro}') cannot run the Lamco RDP Server, and xrdp cannot serve a Wayland desktop. Selected: NONE — the VM is deployed console/SSH only.",
                        shell.VmName, detected?.ToString() ?? "unreadable");
            }
            else if (resolved == RdpBackend.Lamco)
            {
                logger.LogInformation(
                    "Auto-select RDP backend: VM {VMName} uses a Wayland session by default and runs {Distro} (Lamco-capable). Selected: LAMCO RDP SERVER.",
                    shell.VmName, detected!.Value);
            }
            else if (resolved == RdpBackend.None && detected == LinuxDistro.NixOs)
            {
                logger.LogWarning(
                    "Auto-select RDP backend: VM {VMName} runs {Distro} (NixOS / Athena Nix). Selected: NONE — no installable RDP backend exists on the nix store (package-manager post-boot installs are structurally impossible; the fork pipeline ships no nix package; xrdp cannot be installed either). The VM is deployed console/SSH only; pick another distro if RDP is required.",
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

        /// <summary>Oldest Plasma wayrdp's KDE backend accepts (its preflight refuses older KWin).</summary>
        internal static readonly (int Major, int Minor) WayrdpMinPlasma = (6, 3);

        /// <summary>
        /// Parses the <c>RDP_PLASMA=&lt;kwin version&gt;</c> line (last
        /// occurrence) into major.minor. Null when absent or unparseable.
        /// </summary>
        internal static (int Major, int Minor)? ParsePlasmaLine(string output)
        {
            (int, int)? last = null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("RDP_PLASMA=", StringComparison.Ordinal))
                    continue;
                var parts = trimmed["RDP_PLASMA=".Length..].Split('.');
                if (parts.Length >= 2
                    && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major)
                    && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int minor))
                {
                    last = (major, minor);
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