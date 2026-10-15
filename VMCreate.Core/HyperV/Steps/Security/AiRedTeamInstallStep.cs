using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Installs an AI red-teaming toolset — <c>garak</c> (LLM vulnerability
    /// scanner), <c>PyRIT</c> (Microsoft's Python Risk Identification Toolkit for
    /// generative AI) and <c>promptmap2</c>(automated prompt-injection scanner) —
    /// via pipx, for testing LLMs and LLM-backed applications. Optional; the
    /// tools only run against models the user supplies.
    /// <para>Gated on the <c>omarchy</c> tag, Order 234 (after RDP resolution,
    /// inside the NOPASSWD window). Best-effort: no usable python/pipx base
    /// yields <c>skipped</c>, never a failed deployment.</para>
    /// </summary>
    public class AiRedTeamInstallStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        public string Name => "Install AI red-team tools";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 234;
        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => IsVisibleFor(item)
               && customizations?.DistributionOptions.Any(o =>
                      string.Equals(o.Name, Name, StringComparison.OrdinalIgnoreCase) && o.IsEnabled) == true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation("Installing AI red-team tools (garak, PyRIT, promptmap2) on VM {VMName}...", shell.VmName);

            try
            {
                // CopyContentAsync lands the script as root:root 0644: world
                // readable, so bash runs it directly (a chmod by the SSH user
                // EPERMs), but only root can remove it — hence sudo rm.
                string script = ScriptResourceLoader.Load("ai_redteam_install.sh");
                string guestScript = $"/tmp/ai_redteam_install_{Guid.NewGuid():N}.sh";
                await shell.CopyContentAsync(script, guestScript, ct);

                string result = await shell.RunCommandAsync(
                    $"bash {guestScript}; rc=$?; sudo rm -f {guestScript}; exit $rc",
                    TimeSpan.FromMinutes(20), ct);

                switch (ParseResult(result))
                {
                    case "ok":
                        logger.LogInformation("AI red-team tools installed on VM {VMName}.", shell.VmName);
                        break;
                    case "skipped":
                        logger.LogWarning("AI red-team install SKIPPED on VM {VMName} — no python/pipx base. {Result}", shell.VmName, result.Trim());
                        break;
                    default:
                        logger.LogWarning("AI red-team install on VM {VMName} completed DEGRADED (one or more tools failed): {Result}", shell.VmName, result.Trim());
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
                    "AI red-team install failed on VM {VMName} — optional step, the deployment continues: {Message}",
                    shell.VmName, ex.Message);
            }
        }

        public string CardTitle => "AI Red-Team Tools";
        public string CardDescription => "Install garak, PyRIT and promptmap2 — tools for vulnerability-scanning and prompt-injection-testing LLMs and LLM-backed applications.";
        public string Label => "Install AI red-team tools (optional)";
        public string Tooltip => "garak (LLM vuln scanner), PyRIT (genAI risk identification), promptmap2 (prompt-injection scanner), each in an isolated pipx environment. They only exercise models you supply.";
        public bool DefaultEnabled => false;
        public bool IsOptional => true;

        public string DeployTitle => "Install AI Red-Team Tools";
        public string DeployDescription => "Installing garak, PyRIT and promptmap2 via pipx";
        public string DeployPhaseId => "Sub_AiRedTeamInstall";
        public string DeployIconName => "Beaker24";
        public int DeployOrder => 234;
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem? item) => item.HasTag("omarchy");

        internal static string? ParseResult(string output)
        {
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("AIREdTEAM_RESULT=", StringComparison.Ordinal))
                {
                    var v = t["AIREdTEAM_RESULT=".Length..];
                    if (v is "ok" or "skipped" or "degraded") last = v;
                }
            }
            return last;
        }
    }
}
