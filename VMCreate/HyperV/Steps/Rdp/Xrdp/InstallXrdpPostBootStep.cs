using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Installs xrdp post-boot over SSH when the RDP backend was not
    /// pre-installed via the cloning-ISO chroot.
    /// <para>
    /// Under <see cref="RdpBackend.Auto"/> the chroot xrdp install is
    /// skipped entirely (no <c>VMCREATE_XRDP</c> KVP — see
    /// <see cref="IsoBootCycleRunner.SendCustomizationFlags"/>), because
    /// pre-boot nobody knows whether xrdp or the Lamco RDP Server is the
    /// right backend, and pre-installing xrdp unconditionally would collide
    /// with Lamco on port 3389. When
    /// <see cref="AutoRdpBackendResolveStep"/> (order 232) resolves Auto →
    /// <see cref="RdpBackend.Xrdp"/>, this step (order 236) backfills the
    /// install that the chroot step would have performed.
    /// </para>
    /// <para>
    /// It also runs for explicit <see cref="RdpBackend.Xrdp"/> deployments
    /// as a safety net: the chroot install is the primary path, but the
    /// post-boot idempotent verify costs one <c>dpkg -s</c> and recovers a
    /// chroot install that silently failed — xrdp MUST exist before
    /// <see cref="FixXrdpStep"/> (245) configures it.
    /// </para>
    /// <para>
    /// Output contract of <c>install_xrdp_postboot.sh</c>: the terminal
    /// line <c>XRDP_POSTBOOT_RESULT=installed|already</c>. Hard failures
    /// exit non-zero before the line appears (RunCommandAsync throws on
    /// the host — never fabricated success); a missing line on a zero-exit
    /// run fails loudly here.
    /// </para>
    /// <para>
    /// Gating: only runs when <see cref="VmCustomizations.RdpBackend"/> is
    /// <see cref="RdpBackend.Xrdp"/> — evaluated AFTER the resolver mutated
    /// the backend in place (PostBootCustomizationService checks
    /// IsApplicable just-in-time per step).
    /// </para>
    /// </summary>
    public class InstallXrdpPostBootStep : ICustomizationStep
    {
        public string Name => "Install xrdp (post-boot)";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 236;
        public string? ProgressPhaseId => "Sub_InstallXrdpPostBoot";

        public bool IsApplicable(GalleryItem item, VmCustomizations? customizations)
            => customizations?.RdpBackend == RdpBackend.Xrdp;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation("Installing xrdp post-boot on VM {VMName}", shell.VmName);

            string script = ScriptResourceLoader.Load("install_xrdp_postboot.sh");

            // /tmp hardening: unpredictable GUID path (no pre-creation /
            // TOCTOU), then ownership+perms tightened before root execution
            // — the same contract as every shipped guest script.
            string guestScript = $"/tmp/install_xrdp_postboot_{Guid.NewGuid():N}.sh";
            await shell.CopyContentAsync(script, guestScript, ct);

            // apt/dnf/pacman installs run beyond the default command
            // timeout; 20 minutes matches the InstallLamcoRdpStep budget.
            string result = await shell.RunCommandAsync(
                $"sudo chown root:root {guestScript} && sudo chmod 0700 {guestScript} && sudo bash {guestScript} && sudo rm -f {guestScript}",
                TimeSpan.FromMinutes(20), ct);

            string? outcome = ParseResultLine(result);
            switch (outcome)
            {
                case "installed":
                    logger.LogInformation("xrdp installed post-boot on VM {VMName}.", shell.VmName);
                    break;
                case "already":
                    logger.LogInformation("xrdp was already installed on VM {VMName} (chroot install) — nothing to do.", shell.VmName);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"xrdp post-boot install on VM {shell.VmName} reported no XRDP_POSTBOOT_RESULT line. " +
                        "The script exited zero but did not confirm success — treating as failure.");
            }
        }

        /// <summary>
        /// Parses the terminal <c>XRDP_POSTBOOT_RESULT=installed|already</c>
        /// line (last occurrence). Null when the line is absent.
        /// </summary>
        private static string? ParseResultLine(string output)
        {
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("XRDP_POSTBOOT_RESULT=", StringComparison.Ordinal))
                {
                    var value = trimmed["XRDP_POSTBOOT_RESULT=".Length..];
                    if (value.Equals("installed", StringComparison.Ordinal)
                        || value.Equals("already", StringComparison.Ordinal))
                    {
                        last = value;
                    }
                }
            }
            return last;
        }
    }
}