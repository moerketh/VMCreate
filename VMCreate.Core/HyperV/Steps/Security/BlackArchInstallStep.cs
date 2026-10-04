using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Adds the BlackArch repositories to an Arch Linux deployment and
    /// installs a curated subset of pentesting tools — the engine behind the
    /// "BlackArch" gallery item (tag: <c>blackarch</c>), which is plain Arch
    /// cloud image plus this post-boot step.
    /// <para>
    /// This mirrors the "Kali … (KDE)" twin model: the distro+repos is a
    /// property of the chosen image (a tag), the deploy card renders
    /// automatically from <see cref="IDistributionOptionMetadata"/>, and no
    /// extra wizard plumbing is needed. The step is required (not
    /// user-togglable) — choosing "BlackArch" and shipping plain Arch would
    /// be a fabricated result.
    /// </para>
    /// <para>
    /// The script bootstraps the BlackArch repo via upstream's
    /// <c>strap.sh</c>, syncs package databases, then installs the
    /// <c>blackarch-pentest</c> group plus a small set of common individual
    /// tools. It emits a terminal <c>BLACKARCH_RESULT=ok|degraded</c>
    /// contract line. A failed repo bootstrap or a failed package install
    /// exits non-zero and fails the deployment — a half-converted image
    /// must never ship silently. "degraded" means the repo is in place and
    /// the core groups installed but a best-effort extra package failed.
    /// </para>
    /// <para>
    /// Runs at Order 230, immediately BEFORE <see cref="DesktopSwitchStep"/>
    /// (231): the BlackArch repos must be in place before the desktop switch
    /// drives pacman, so a desktop chosen in the wizard resolves against the
    /// combined Arch+BlackArch package set. Both steps run before
    /// <see cref="AutoRdpBackendResolveStep"/> (232), so the Auto RDP
    /// detection then sees the post-switch default session.
    /// </para>
    /// </summary>
    public class BlackArchInstallStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "Add BlackArch Repositories and Tools";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 230;

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => IsVisibleFor(item);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation(
                "Converting VM {VMName} to BlackArch (adding repos, installing pentest tools)...", shell.VmName);

            string script = ScriptResourceLoader.Load("blackarch_install.sh");

            // /tmp hardening: unpredictable GUID path (no pre-creation /
            // TOCTOU), then ownership+perms tightened before root execution
            // — the same contract as every shipped guest script.
            string guestScript = $"/tmp/blackarch_install_{Guid.NewGuid():N}.sh";
            await shell.CopyContentAsync(script, guestScript, ct);

            // Repository sync + a curated tool-group install is a large pacman
            // transaction; 30 minutes matches the other big-install budgets.
            string result = await shell.RunCommandAsync(
                $"sudo chown root:root {guestScript} && sudo chmod 0700 {guestScript} && sudo bash {guestScript} && sudo rm -f {guestScript}",
                TimeSpan.FromMinutes(30), ct);

            switch (ParseResultLine(result))
            {
                case "ok":
                    logger.LogInformation(
                        "BlackArch repos added and tools installed on VM {VMName}.", shell.VmName);
                    break;
                case "degraded":
                    logger.LogWarning(
                        "BlackArch conversion on VM {VMName} completed DEGRADED — the repo and core tools are in place, " +
                        "but a best-effort extra package failed (details in the deployment log): {Result}",
                        shell.VmName, result.Trim());
                    break;
                default:
                    throw new InvalidOperationException(
                        $"BlackArch install on VM {shell.VmName} reported no BLACKARCH_RESULT line. " +
                        "The script exited zero but did not confirm success — treating as failure.");
            }
        }

        // ── IConfigurableCustomizationStep (UI metadata) ────────────────
        public string CardTitle => "BlackArch Repositories & Tools";
        public string CardDescription => "Add the BlackArch repositories to the Arch base and install a curated subset of pentesting tools. Runs automatically for the BlackArch gallery image.";
        public string Label => "Install BlackArch tools (required)";
        public string Tooltip => "Bootstraps the BlackArch repo via strap.sh, syncs package databases, and installs the blackarch-pentest group plus common tools. This is a required step for the BlackArch image.";
        public bool DefaultEnabled => true;
        public bool IsOptional => false;

        // ── IDistributionOptionMetadata (deploy-phase UI) ───────────────
        public string DeployTitle => "Install BlackArch Tools";
        public string DeployDescription => "Adding the BlackArch repositories and installing a curated subset of pentesting tools";
        public string DeployPhaseId => "Sub_BlackArchInstall";
        public string DeployIconName => "Shield24";
        public int DeployOrder => 230;
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem? item)
            => item.HasTag("blackarch");

        /// <summary>
        /// Parses the terminal <c>BLACKARCH_RESULT=ok|degraded</c> line
        /// (last occurrence). Null when the line is absent.
        /// </summary>
        internal static string? ParseResultLine(string output)
        {
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("BLACKARCH_RESULT=", StringComparison.Ordinal))
                {
                    var value = trimmed["BLACKARCH_RESULT=".Length..];
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
