using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Switches a Kali Linux deployment from the stock XFCE desktop to KDE
    /// Plasma — the engine behind the "Kali … (KDE)" gallery twins
    /// (tag: <c>kali-kde</c>).
    /// <para>
    /// A separate gallery product beats a "desktop flavor" wizard control:
    /// the flavor is a property of the chosen image (like Flare-VM being a
    /// Windows option), the deploy card renders automatically from
    /// <see cref="IDistributionOptionMetadata"/>, and no extra plumbing is
    /// needed beyond the tag + this step. The step is required (not
    /// user-togglable) for the KDE items — choosing "Kali … (KDE)" and
    /// shipping XFCE would be a fabricated result.
    /// </para>
    /// <para>
    /// Runs at Order 231, deliberately BEFORE
    /// <see cref="AutoRdpBackendResolveStep"/> (232): after the switch,
    /// Kali's session default is Plasma — a Wayland-default desktop in Kali
    /// 2023.1+ (Plasma 6) — so the Auto RDP backend detection sees the new
    /// Wayland session and selects the Lamco RDP Server for these items.
    /// (Explicit backend choices are unaffected; they are gated on
    /// <see cref="VmCustomizations.RdpBackend"/>, not the desktop.)
    /// </para>
    /// <para>
    /// Output contract of <c>kali_kde_switch.sh</c>: the terminal line
    /// <c>KALI_KDE_RESULT=ok|degraded</c>. Hard failures (apt install of
    /// the desktop failing) exit non-zero and fail the deployment — a
    /// half-installed desktop must never ship silently. "degraded" means
    /// the switch succeeded but a side task (e.g. setting the default
    /// session manager alternative) failed.
    /// </para>
    /// </summary>
    public class KaliKdeSwitchStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "Switch Kali to KDE Plasma";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 231;

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => IsVisibleFor(item);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation(
                "Switching VM {VMName} from XFCE to the KDE Plasma desktop...", shell.VmName);

            string script = ScriptResourceLoader.Load("kali_kde_switch.sh");

            // /tmp hardening: unpredictable GUID path (no pre-creation /
            // TOCTOU), then ownership+perms tightened before root execution
            // — the same contract as every shipped guest script.
            string guestScript = $"/tmp/kali_kde_switch_{Guid.NewGuid():N}.sh";
            await shell.CopyContentAsync(script, guestScript, ct);

            // kali-desktop-kde is a multi-GB meta-package (desktop + apps);
            // 20 minutes matches the other big-install budgets.
            string result = await shell.RunCommandAsync(
                $"sudo chown root:root {guestScript} && sudo chmod 0700 {guestScript} && sudo bash {guestScript} && sudo rm -f {guestScript}",
                TimeSpan.FromMinutes(20), ct);

            switch (ParseResultLine(result))
            {
                case "ok":
                    logger.LogInformation(
                        "Kali desktop switch to KDE Plasma completed on VM {VMName}.", shell.VmName);
                    break;
                case "degraded":
                    logger.LogWarning(
                        "Kali desktop switch to KDE Plasma completed DEGRADED on VM {VMName} — the desktop itself switched, " +
                        "but a side task failed (details in the deployment log): {Result}",
                        shell.VmName, result.Trim());
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Kali KDE switch on VM {shell.VmName} reported no KALI_KDE_RESULT line. " +
                        "The script exited zero but did not confirm success — treating as failure.");
            }
        }

        // ── IConfigurableCustomizationStep (UI metadata) ────────────────
        public string CardTitle => "KDE Plasma Desktop";
        public string CardDescription => "Install the KDE Plasma desktop on Kali and make it the default session, replacing XFCE. Runs automatically for the KDE gallery images.";
        public string Label => "Switch to KDE Plasma (required)";
        public string Tooltip => "Installs kali-desktop-kde, sets the default session manager to startplasma-wayland, and removes the XFCE desktop. This is a required step for the KDE-flavored Kali images.";
        public bool DefaultEnabled => true;
        public bool IsOptional => false;

        // ── IDistributionOptionMetadata (deploy-phase UI) ───────────────
        public string DeployTitle => "Switch to KDE Plasma";
        public string DeployDescription => "Installing the KDE Plasma desktop and making it the default session";
        public string DeployPhaseId => "Sub_KaliKdeSwitch";
        public string DeployIconName => "Desktop24";
        public int DeployOrder => 231;
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem? item)
            => item.HasTag("kali-kde");

        /// <summary>
        /// Parses the terminal <c>KALI_KDE_RESULT=ok|degraded</c> line
        /// (last occurrence). Null when the line is absent.
        /// </summary>
        internal static string? ParseResultLine(string output)
        {
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("KALI_KDE_RESULT=", StringComparison.Ordinal))
                {
                    var value = trimmed["KALI_KDE_RESULT=".Length..];
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