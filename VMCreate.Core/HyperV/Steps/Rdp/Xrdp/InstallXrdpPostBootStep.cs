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
    /// skipped entirely (no <c>VMCREATE_XRDP</c> flag to the ISO cycle),
    /// because
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
    /// lines <c>XRDP_POSTBOOT_RESULT=installed|already</c> and
    /// <c>XRDP_POSTBOOT_TRANSPORT=vsock|tcp|unverified</c>. Hard failures
    /// exit non-zero before the lines appear (RunCommandAsync throws on
    /// the host — never fabricated success); a missing RESULT line on a
    /// zero-exit run fails loudly here. A <c>tcp</c> TRANSPORT (distro
    /// xrdp built without vsock — rolled back by the script) is a loud
    /// warning, not a failure: the image keeps network RDP.
    /// </para>
    /// <para>
    /// Gating: only runs when <see cref="VmCustomizations.RdpBackend"/> is
    /// <see cref="RdpBackend.Xrdp"/> — evaluated AFTER the resolver mutated
    /// the backend in place (PostBootCustomizationService checks
    /// IsApplicable just-in-time per step) — AND the item is not a
    /// Nix-family guest (<see cref="LinuxDistro.NixOs"/>): the nix store
    /// has no apt/dnf/zypper/pacman install path for the package list
    /// this step's script needs, so the script exits 1 and the
    /// deployment hard-fails.
    /// <see cref="AutoRdpBackendResolveStep"/> (order 232) resolves Nix
    /// guests to <see cref="RdpBackend.None"/> under Auto, so this gate
    /// is the safety net for a user explicitly forcing xrdp on Nix.
    /// </para>
    /// </summary>
    public class InstallXrdpPostBootStep : ICustomizationStep, IStepDisplayInfo
    {
        public string Name => "Install xrdp (post-boot)";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 236;
        public string? ProgressPhaseId => "Sub_InstallXrdpPostBoot";

        public string DeployTitle => "Install xrdp (post-boot)";
        public string DeployDescription => "Verifying or backfilling the xrdp install";
        public string DeployIconName => "Desktop24";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => !item.HasTag("headless")
               && customizations?.RdpBackend == RdpBackend.Xrdp
               && item?.LinuxDistro != LinuxDistro.NixOs;

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

            string? transport = ParseResultLine(result, "XRDP_POSTBOOT_TRANSPORT=", new[] { "vsock", "tcp", "unverified" });
            switch (transport)
            {
                case "vsock":
                    logger.LogInformation(
                        "xrdp on VM {VMName} listens on vsock — Hyper-V Enhanced Session is available.",
                        shell.VmName);
                    break;
                case "tcp":
                    logger.LogWarning(
                        "xrdp on VM {VMName} could not start with the vsock listener (distro package likely built " +
                        "without vsock support) and was rolled back to its TCP config. Network RDP works; Hyper-V " +
                        "Enhanced Session (the vmconnect popup) will NOT appear for this VM.",
                        shell.VmName);
                    break;
                default:
                    logger.LogWarning(
                        "xrdp on VM {VMName} reported no XRDP_POSTBOOT_TRANSPORT line — the Enhanced Session " +
                        "listener could not be verified (no systemctl?). vmconnect may fall back to the basic console.",
                        shell.VmName);
                    break;
            }
        }

        /// <summary>
        /// Parses the terminal <c>XRDP_POSTBOOT_RESULT=installed|already</c>
        /// line (last occurrence). Null when the line is absent.
        /// </summary>
        private static string? ParseResultLine(string output)
            => ParseResultLine(output, "XRDP_POSTBOOT_RESULT=", new[] { "installed", "already" });

        /// <summary>
        /// Parses a terminal <c>PREFIX=one-of-allowed</c> line (last
        /// occurrence). Null when the line is absent or the value is not
        /// one of the allowed tokens.
        /// </summary>
        private static string? ParseResultLine(string output, string prefix, string[] allowed)
        {
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var value = trimmed[prefix.Length..];
                    if (Array.IndexOf(allowed, value) >= 0)
                    {
                        last = value;
                    }
                }
            }
            return last;
        }
    }
}