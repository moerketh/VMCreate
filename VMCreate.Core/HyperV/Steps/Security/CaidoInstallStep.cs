using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Installs Caido — a modern, Rust-based web security proxy (Burp
    /// alternative) — as an optional desktop app for the Omarchy-based deployment.
    /// Prefers the AUR <c>caido-desktop</c> package (Omarchy ships paru) and falls
    /// back to the official AppImage. Optional because Caido is proprietary
    /// freemium software; the card is opt-in.
    /// <para>Gated on the <c>omarchy</c> tag, Order 235 (inside the NOPASSWD
    /// window). Best-effort: no network/install source yields
    /// <c>skipped</c>.</para>
    /// </summary>
    public class CaidoInstallStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        public string Name => "Install Caido web proxy";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 235;
        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => IsVisibleFor(item)
               && customizations?.DistributionOptions.Any(o =>
                      string.Equals(o.Name, Name, StringComparison.OrdinalIgnoreCase) && o.IsEnabled) == true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation("Installing Caido web proxy on VM {VMName}...", shell.VmName);

            try
            {
                // CopyContentAsync lands the script as root:root 0644: world
                // readable, so bash runs it directly (a chmod by the SSH user
                // EPERMs), but only root can remove it — hence sudo rm.
                string script = ScriptResourceLoader.Load("caido_install.sh");
                string guestScript = $"/tmp/caido_install_{Guid.NewGuid():N}.sh";
                await shell.CopyContentAsync(script, guestScript, ct);

                string result = await shell.RunCommandAsync(
                    $"bash {guestScript}; rc=$?; sudo rm -f {guestScript}; exit $rc",
                    TimeSpan.FromMinutes(20), ct);

                switch (ParseResult(result))
                {
                    case "ok":
                        logger.LogInformation("Caido installed on VM {VMName}.", shell.VmName);
                        break;
                    case "skipped":
                        logger.LogWarning("Caido install SKIPPED on VM {VMName} — no install source available. {Result}", shell.VmName, result.Trim());
                        break;
                    default:
                        logger.LogWarning("Caido install on VM {VMName} completed DEGRADED (details in the deployment log): {Result}", shell.VmName, result.Trim());
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // SshTransport throws untyped Exceptions; per the class doc
                // this best-effort step never fails the deployment.
                logger.LogWarning(ex,
                    "Caido install failed on VM {VMName} — optional step, the deployment continues: {Message}",
                    shell.VmName, ex.Message);
            }
        }

        public string CardTitle => "Caido Web Proxy";
        public string CardDescription => "Install Caido — a lightweight, modern web security auditing proxy (a Burp Suite alternative) with an extensible workflow engine.";
        public string Label => "Install Caido (optional)";
        public string Tooltip => "Installs Caido via the AUR caido-desktop package (paru) or the official AppImage. Caido is proprietary freemium software — a free account is required for full use.";
        public bool DefaultEnabled => false;
        public bool IsOptional => true;

        public string DeployTitle => "Install Caido";
        public string DeployDescription => "Installing the Caido web security proxy";
        public string DeployPhaseId => "Sub_CaidoInstall";
        public string DeployIconName => "GlobeSearch24";
        public int DeployOrder => 235;
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem? item) => item.HasTag("omarchy");

        internal static string? ParseResult(string output)
        {
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("CAIDO_RESULT=", StringComparison.Ordinal))
                {
                    var v = t["CAIDO_RESULT=".Length..];
                    if (v is "ok" or "skipped" or "degraded") last = v;
                }
            }
            return last;
        }
    }
}
