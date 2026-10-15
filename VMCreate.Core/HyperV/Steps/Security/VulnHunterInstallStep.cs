using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Installs <c>nealbridges/VulnHunter</c> — an agentic AI security scanner —
    /// into an Omarchy deployment by cloning the repo and running its
    /// harness-portable installer pointed at the detected agent harness's skills
    /// directory. Optional (user-togglable): model access is user-supplied at
    /// run time, so the tool is inert until a harness + credentials exist.
    /// <para>
    /// Gated on the <c>omarchy</c> tag. Runs at Order 233 — immediately after
    /// <c>AutoRdpBackendResolveStep</c> (232) — inside the temporary NOPASSWD
    /// sudo window (45→880) so it can write to /opt while still SSH-driven.
    /// Best-effort: missing prerequisites (python3.11+, a harness skills dir)
    /// yield <c>skipped</c>, never a failed deployment.
    /// </para>
    /// </summary>
    public class VulnHunterInstallStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        public string Name => "Install VulnHunter (agentic AI scanner)";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 233;
        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => IsVisibleFor(item)
               && customizations?.DistributionOptions.Any(o =>
                      string.Equals(o.Name, Name, StringComparison.OrdinalIgnoreCase) && o.IsEnabled) == true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation("Installing VulnHunter (agentic AI scanner) on VM {VMName}...", shell.VmName);

            try
            {
                // CopyContentAsync lands the script as root:root 0644: world
                // readable, so bash runs it directly (a chmod by the SSH user
                // EPERMs), but only root can remove it — hence sudo rm.
                string script = ScriptResourceLoader.Load("vulnhunter_install.sh");
                string guestScript = $"/tmp/vulnhunter_install_{Guid.NewGuid():N}.sh";
                await shell.CopyContentAsync(script, guestScript, ct);

                string result = await shell.RunCommandAsync(
                    $"bash {guestScript}; rc=$?; sudo rm -f {guestScript}; exit $rc",
                    TimeSpan.FromMinutes(20), ct);

                switch (ParseResult(result))
                {
                    case "ok":
                        logger.LogInformation("VulnHunter installed on VM {VMName}.", shell.VmName);
                        break;
                    case "skipped":
                        logger.LogWarning("VulnHunter install SKIPPED on VM {VMName} — missing prerequisite (harness/python); install later via /opt/VulnHunter. {Result}", shell.VmName, result.Trim());
                        break;
                    default:
                        logger.LogWarning("VulnHunter install on VM {VMName} completed DEGRADED (details in the deployment log): {Result}", shell.VmName, result.Trim());
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
                    "VulnHunter install failed on VM {VMName} — optional step, the deployment continues: {Message}",
                    shell.VmName, ex.Message);
            }
        }

        // ── IConfigurableCustomizationStep ────────────────────────────────
        public string CardTitle => "VulnHunter (Agentic AI Scanner)";
        public string CardDescription => "Clone and install nealbridges/VulnHunter — an agentic AI security scanner that hunts exploitable vulnerabilities and drives executable PoCs through your agent harness.";
        public string Label => "Install VulnHunter (optional)";
        public string Tooltip => "Clones VulnHunter and runs its harness-portable installer, pointing skills at your Omarchy agent harness. Model access is user-supplied — the tool stays inert until you connect a model.";
        public bool DefaultEnabled => false;
        public bool IsOptional => true;

        // ── IDistributionOptionMetadata ───────────────────────────────────
        public string DeployTitle => "Install VulnHunter";
        public string DeployDescription => "Cloning and installing the VulnHunter agentic AI scanner";
        public string DeployPhaseId => "Sub_VulnHunterInstall";
        public string DeployIconName => "Shield24";
        public int DeployOrder => 233;
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem? item) => item.HasTag("omarchy");

        internal static string? ParseResult(string output)
        {
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("VULNHUNTER_RESULT=", StringComparison.Ordinal))
                {
                    var v = t["VULNHUNTER_RESULT=".Length..];
                    if (v is "ok" or "skipped" or "degraded") last = v;
                }
            }
            return last;
        }
    }
}
