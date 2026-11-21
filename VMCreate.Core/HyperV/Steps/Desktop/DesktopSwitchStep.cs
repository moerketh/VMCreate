using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Switches a Linux guest's desktop environment — the engine behind the
    /// desktop-environment card on the customization page
    /// (<see cref="VmCustomizations.DesktopEnvironmentId"/>). Replaces the
    /// per-flavor gallery twins ("Kali … (KDE)"): one gallery item per
    /// distro, any desktop the distro's <see cref="IDesktopEnvironmentCatalog"/>
    /// offers.
    /// <para>
    /// Chroot-capable: the switch runs in the ISO chroot when the deployment
    /// has an ISO stage, post-boot otherwise (the cloud-init flow). With no
    /// desktop running during the swap, nothing can log into the half-built
    /// desktop. (Live: Enhanced Session reached the image's stock xrdp
    /// mid-switch, xrdp started the new KDE nested on its own X server, and
    /// the orphaned session outlived xrdp and wedged the Lamco install.) The
    /// first boot then comes up in the chosen desktop directly. Both scripts
    /// only touch packages, files and enable-state there; the desktops come
    /// from the distro's own repos (on BlackArch the official Arch repos, so
    /// the post-boot BlackArch strap, Order 230, is no prerequisite).
    /// </para>
    /// <para>
    /// Runs at Order 231, deliberately BEFORE
    /// <see cref="AutoRdpBackendResolveStep"/> (232) and the X11 fixups
    /// (240-270), which wait while a switch is pending
    /// (<see cref="VmCustomizations.AutoRdpAwaitsDesktopSwitch"/>) and run
    /// right after it, in the same context: the executors check
    /// applicability just-in-time. The switch records the display server it
    /// landed on (<see cref="VmCustomizations.SwitchedDesktopDisplayServer"/>)
    /// for the resolver. Explicit backend choices are unaffected.
    /// </para>
    /// <para>
    /// The script is parameterized by this step: a variable block naming the
    /// target desktop's packages, session starter and display manager is
    /// injected below the shebang. The script family is selected by the
    /// catalog's <see cref="IDesktopEnvironmentCatalog.PackageManager"/> —
    /// <c>desktop_switch_apt.sh</c> for apt distros (Kali),
    /// <c>desktop_switch_pacman.sh</c> for Arch (BlackArch). Output
    /// contract: the terminal line <c>DESKTOP_SWITCH_RESULT=ok|degraded</c>.
    /// Hard failures (missing metapackage in the distro's live index, the
    /// package install failing) exit non-zero and fail the deployment — a
    /// half-installed desktop must never ship silently. "degraded" means the
    /// switch succeeded but a side task (session-manager alternative,
    /// display-manager handover, old desktop purge) failed.
    /// </para>
    /// </summary>
    public class DesktopSwitchStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        private readonly IReadOnlyList<IDesktopEnvironmentCatalog> _catalogs;

        public DesktopSwitchStep(IEnumerable<IDesktopEnvironmentCatalog>? catalogs)
        {
            _catalogs = catalogs?.ToArray() ?? Array.Empty<IDesktopEnvironmentCatalog>();
        }

        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "Switch Desktop Environment";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 231;
        public bool SupportsChroot => true;

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
        {
            if (customizations?.DesktopEnvironmentId == null)
                return false;
            var option = _catalogs.ForItem(item)?.Resolve(customizations.DesktopEnvironmentId);
            return option != null;
        }

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var catalog = _catalogs.ForItem(item)
                ?? throw new InvalidOperationException(
                    $"Desktop switch requested on VM {shell.VmName} but no desktop catalog " +
                    $"applies to {item.Name}. The image and the desktop selection have diverged.");

            var option = catalog.Resolve(customizations.DesktopEnvironmentId)
                ?? throw new InvalidOperationException(
                    $"Desktop switch requested on VM {shell.VmName} but desktop " +
                    $"'{customizations.DesktopEnvironmentId}' is not offered for {item.Name}. " +
                    "The desktop selection and the image have diverged — refusing to guess.");

            logger.LogInformation(
                "Switching VM {VMName} to the {Desktop} desktop ({DesktopId}) via {PackageManager} ({Context})...",
                shell.VmName, option.DisplayName, option.Id, catalog.PackageManager, shell.Context);

            string script = BuildScript(option, catalog.PackageManager, shell.Context);

            // /tmp hardening: unpredictable GUID path (no pre-creation /
            // TOCTOU), then ownership+perms tightened before root execution
            // — the same contract as every shipped guest script.
            string guestScript = $"/tmp/desktop_switch_{Guid.NewGuid():N}.sh";
            await shell.CopyContentAsync(script, guestScript, ct);

            // Desktop metapackages are multi-GB (desktop + apps);
            // 20 minutes matches the other big-install budgets.
            string result = await shell.RunCommandAsync(
                $"sudo chown root:root {guestScript} && sudo chmod 0700 {guestScript} && sudo bash {guestScript} && sudo rm -f {guestScript}",
                TimeSpan.FromMinutes(20), ct);

            switch (ParseResultLine(result))
            {
                case "ok":
                    logger.LogInformation(
                        "Desktop switch to {Desktop} completed on VM {VMName}.",
                        option.DisplayName, shell.VmName);
                    break;
                case "degraded":
                    logger.LogWarning(
                        "Desktop switch to {Desktop} completed DEGRADED on VM {VMName} — the desktop itself switched, " +
                        "but a side task failed (details in the deployment log): {Result}",
                        option.DisplayName, shell.VmName, result.Trim());
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Desktop switch on VM {shell.VmName} reported no DESKTOP_SWITCH_RESULT line. " +
                        "The script exited zero but did not confirm success — treating as failure.");
            }

            // The switch has landed: clear the pending marker so the RDP
            // auto-resolver (order 232, gated on "no pending switch") runs
            // live against the SWITCHED desktop instead of deferring.
            customizations.DesktopEnvironmentId = null;
            customizations.SwitchedDesktopDisplayServer = option.DisplayServer;
        }

        // ── IConfigurableCustomizationStep (UI metadata) ────────────────
        // The customize page renders the desktop picker as its own card
        // (exclusive radio group, not a checkbox), driven directly by the
        // catalog; this step's card metadata only surfaces on the deploy
        // page, so the texts are desktop-agnostic.
        public string CardTitle => "Desktop Environment";
        public string CardDescription => "Switch the VM's desktop environment during deployment. Choose the desktop on the customization page.";
        public string Label => "Switch desktop environment";
        public string Tooltip => "Installs the selected desktop environment (e.g. KDE Plasma, GNOME) before the VM's first boot and makes it the default session, before the RDP backend is resolved.";
        public bool DefaultEnabled => false;
        public bool IsOptional => false;

        // ── IDistributionOptionMetadata (deploy-phase UI) ───────────────
        public string DeployTitle => "Switch Desktop Environment";
        public string DeployDescription => "Installing the selected desktop environment and making it the default session";
        public string DeployPhaseId => "Sub_DesktopSwitch";
        public string DeployIconName => "Desktop24";
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem? item)
            => _catalogs.ForItem(item) != null;

        /// <summary>
        /// Maps a catalog <see cref="DesktopPackageManager"/> to its embedded
        /// switch script. Only families with a shipped script are supported —
        /// a catalog advertising an unsupported manager fails the deployment
        /// loudly rather than guessing.
        /// </summary>
        private static string ScriptResourceFor(DesktopPackageManager packageManager)
            => packageManager switch
            {
                DesktopPackageManager.Apt => "desktop_switch_apt.sh",
                DesktopPackageManager.Pacman => "desktop_switch_pacman.sh",
                _ => throw new NotSupportedException(
                    $"No desktop-switch script ships for package manager '{packageManager}'. " +
                    "A catalog must not advertise a package manager without a matching script."),
            };

        /// <summary>
        /// Builds the guest script: the embedded switch script for the
        /// catalog's package manager with a parameter block naming the target
        /// desktop injected directly below the shebang (injected values come
        /// from the catalog, never free-form user input — and are still
        /// shell-quoted and validated).
        /// </summary>
        internal static string BuildScript(
            DesktopEnvironmentOption option, DesktopPackageManager packageManager,
            GuestExecutionContext context = GuestExecutionContext.Live)
        {
            string resourceName = ScriptResourceFor(packageManager);
            string script = ScriptResourceLoader.Load(resourceName);

            var packages = ShellQuoteList(option.Packages);
            var packagesToRemove = ShellQuoteList(option.PackagesToRemove);
            string parameters =
                $"DESKTOP_PACKAGES='{packages}'\n" +
                $"DESKTOP_PACKAGES_TO_REMOVE='{packagesToRemove}'\n" +
                $"DESKTOP_SESSION_STARTER='{ShellQuote(option.SessionStarter ?? "")}'\n" +
                $"DESKTOP_DISPLAY_MANAGER='{ShellQuote(option.DisplayManager ?? "")}'\n" +
                $"DESKTOP_DISPLAY_SERVER='{(option.DisplayServer == DesktopDisplayServer.X11Only ? "x11" : "wayland")}'\n" +
                $"DESKTOP_SWITCH_CONTEXT='{(context == GuestExecutionContext.Chroot ? "chroot" : "live")}'\n";

            int firstNewline = script.IndexOf('\n');
            if (firstNewline < 0)
                throw new InvalidOperationException($"{resourceName} has no shebang line to inject parameters below.");

            return script[..(firstNewline + 1)] + parameters + script[(firstNewline + 1)..];
        }

        private static string ShellQuoteList(IReadOnlyList<string> values)
            => string.Join(" ", values.Select(v => v).Where(v => v.Length > 0).Select(ShellQuote));

        /// <summary>Single-quotes a value for the injected bash block; catalog data must never carry shell metacharacters.</summary>
        private static string ShellQuote(string value)
        {
            if (value.Contains('\'') || value.Contains('\n') || value.Contains('\r'))
                throw new InvalidOperationException(
                    $"Desktop catalog value '{value}' contains characters that cannot be safely passed to the guest script.");
            return value;
        }

        /// <summary>
        /// Parses the terminal <c>DESKTOP_SWITCH_RESULT=ok|degraded</c> line
        /// (last occurrence). Null when the line is absent.
        /// </summary>
        internal static string? ParseResultLine(string output)
        {
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("DESKTOP_SWITCH_RESULT=", StringComparison.Ordinal))
                {
                    var value = trimmed["DESKTOP_SWITCH_RESULT=".Length..];
                    if (value.Equals("ok", StringComparison.Ordinal)
                        || value.Equals("degraded", StringComparison.Ordinal))
                    {
                        last = value;
                    }
                }
            }
            return last;
        }
    }
}
