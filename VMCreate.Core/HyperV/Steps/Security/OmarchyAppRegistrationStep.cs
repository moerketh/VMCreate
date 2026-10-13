using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Surfaces installed security tools and curated web apps as first-class
    /// applications in Omarchy's launcher (quickshell) by writing XDG
    /// <c>.desktop</c> entries into the deploy user's
    /// <c>~/.local/share/applications/</c> and refreshing the desktop database.
    /// <para>
    /// Web apps (CyberChef, revshells.com, jwt.io, GTFOBins, LOLBAS, MITRE
    /// ATT&amp;CK) launch as focused browser app-windows and are chosen to work
    /// from local cache; CLI security tools (nmap, hashcat, john, wireshark) get
    /// <c>Terminal=true</c> wrappers so they are discoverable in the menu. The
    /// site/tool list lives in <c>omarchy_register_apps.sh</c> as simple
    /// pipe-delimited tables, so more entries can be appended in one place.
    /// </para>
    /// <para>
    /// Gated on the <c>omarchy</c> tag and runs at Order 236 — after every
    /// install step (BlackArch repos at 230, the AI-era tools at 233–235) so it
    /// registers whatever actually landed. Required for the Omarchy-based item
    /// (a distro whose tools don't appear in the launcher is a broken result),
    /// but never fails the deployment — a registration miss is cosmetic.
    /// </para>
    /// </summary>
    public class OmarchyAppRegistrationStep : ICustomizationStep, IDistributionOptionMetadata
    {
        public string Name => "Register security apps in launcher";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 236;
        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => IsVisibleFor(item);

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation("Registering security web apps and tools in Omarchy launcher on VM {VMName}...", shell.VmName);

            string script = ScriptResourceLoader.Load("omarchy_register_apps.sh");
            string guestScript = $"/tmp/omarchy_register_apps_{Guid.NewGuid():N}.sh";
            await shell.CopyContentAsync(script, guestScript, ct);

            string result = await shell.RunCommandAsync(
                $"chmod 0755 {guestScript} && bash {guestScript}; rc=$?; rm -f {guestScript}; exit $rc",
                TimeSpan.FromMinutes(10), ct);

            switch (ParseResult(result))
            {
                case "ok":
                    logger.LogInformation("Security apps registered in Omarchy launcher on VM {VMName}.", shell.VmName);
                    break;
                case "skipped":
                    logger.LogWarning("App registration SKIPPED on VM {VMName}: {Result}", shell.VmName, result.Trim());
                    break;
                default:
                    logger.LogWarning("App registration on VM {VMName} completed DEGRADED (some entries may be missing): {Result}", shell.VmName, result.Trim());
                    break;
            }
        }

        public string DeployTitle => "Register Security Apps";
        public string DeployDescription => "Adding security tools and web apps to the Omarchy launcher";
        public string DeployPhaseId => "Sub_OmarchyRegisterApps";
        public string DeployIconName => "Apps24";
        public int DeployOrder => 236;
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem? item) => item.HasTag("omarchy");

        internal static string? ParseResult(string output)
        {
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("APPS_RESULT=", StringComparison.Ordinal))
                {
                    var v = t["APPS_RESULT=".Length..];
                    if (v is "ok" or "skipped" or "degraded") last = v;
                }
            }
            return last;
        }
    }
}
