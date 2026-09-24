using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Bootstraps a temporary passwordless-sudo drop-in
    /// (/etc/sudoers.d/99-vmcreate-nopasswd, mode 440) for the deploy user on
    /// guests whose installed system ships ordinary passworded sudo — today:
    /// Omarchy, an unattended Arch install that grants a stock
    /// <c>omarchy ALL=(ALL:ALL) ALL</c> entry. Once the one-shot
    /// <c>sudo -S</c> succeeds, every later provisioning step can keep its
    /// plain <c>sudo</c> prefix and <c>sudo -n true</c> reachability probe.
    /// </summary>
    /// <remarks>
    /// Password hygiene: the deploy password is consumed ONLY by this step and
    /// travels on ssh's stdin into <c>sudo -S</c> — never on a command line
    /// (ssh argv, guest bash string, guest ps) and never in logs. sudo's own
    /// masking covers the guest-side journal. The drop-in is validated with
    /// visudo before use and removed again by
    /// <see cref="RemovePasswordlessSudoStep"/> (order 880).
    /// <para>
    /// Order 45 runs directly ahead of the first sudo-based step
    /// (RemoveVBoxGuestAdditions at 50) so the whole Linux pipeline inherits
    /// passwordless sudo for the rest of the deployment.
    /// </para>
    /// </remarks>
    public class EnablePasswordlessSudoStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        internal const string DropInPath = "/etc/sudoers.d/99-vmcreate-nopasswd";
        internal const string OmarchyTag = "omarchy";

        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "Bootstrap passwordless sudo";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 45;

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        // Gates on execution too: IsVisibleFor drives the customization-page
        // card plus the WizardFlow page visibility, IsApplicable gates the
        // deploy pipeline itself.
        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => IsVisibleFor(item);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string username = item?.InitialUsername ?? "omarchy";
            if (!UsernameValidator.IsValidLinuxUsername(username))
            {
                throw new InvalidOperationException(
                    $"Gallery item InitialUsername '{username}' is not a valid Linux username. " +
                    "Cannot write a sudoers drop-in for it — deployment would run with a broken sudo policy.");
            }

            string? password = item?.InitialPassword;
            if (string.IsNullOrEmpty(password))
            {
                logger.LogInformation(
                    "Skipping passwordless-sudo bootstrap on VM {VMName}: gallery item has no InitialPassword to authorize the one-shot sudo with.", shell.VmName);
                return;
            }

            // Idempotence: a rerun over a leftover drop-in must not re-prompt
            // (NOPASSWD sudo would answer [sudo] password with garbage on
            // stdin) nor rewrite anything.
            string probe = await shell.RunCommandAsync(
                $"sudo -n test -f {DropInPath} && sudo -n true 2>/dev/null && echo PRESENT || echo ABSENT", ct);
            if (probe.Trim() == "PRESENT")
            {
                logger.LogDebug("Passwordless-sudo drop-in already installed on VM {VMName}.", shell.VmName);
                return;
            }

            logger.LogInformation("Installing temporary NOPASSWD sudo drop-in for '{Username}' on VM {VMName} (password travels on ssh stdin only).", username, shell.VmName);

            // The bash string carries no secret: the password is the single
            // stdin line that sudo -S consumes at its prompt. sudo -k forces
            // the credential prompt even if a timestamp is cached, guaranteeing
            // the prompt — not the inner bash — reads the password line.
            string script =
                "set -e; " +
                $"printf '%s\\n' '{SshTransport.EscapeSingleQuotes(username + " ALL=(ALL:ALL) NOPASSWD: ALL")}' > {DropInPath}; " +
                $"chmod 440 {DropInPath}; " +
                $"visudo -c -f {DropInPath} | grep -q 'parsed OK'";
            string bash = "sudo -S -k bash -c '" + SshTransport.EscapeSingleQuotes(script) + "'";

            await shell.RunCommandWithStdinAsync(bash, password + "\n", ct);

            logger.LogInformation("NOPASSWD sudo drop-in validated and installed on VM {VMName} — later steps use plain sudo.", shell.VmName);
        }

        // ── IConfigurableCustomizationStep (customization-page card) ────
        public string CardTitle => "Passwordless sudo bootstrap";
        public string CardDescription => "The installed Omarchy system grants passworded sudo only. This required step grants the deploy user temporary NOPASSWD sudo (validated with visudo, removed again after provisioning) so all remaining steps can run non-interactively.";
        public string Label => "Grant temporary NOPASSWD sudo (required)";
        public string Tooltip => "Uses the default password once over an encrypted ssh stdin channel — never on a command line or in logs — to install /etc/sudoers.d/99-vmcreate-nopasswd (mode 440). Removed again at the end of provisioning.";
        public bool DefaultEnabled => true;
        public bool IsOptional => false;

        // ── IDistributionOptionMetadata (deployment-page card) ──────────
        public string DeployTitle => "Bootstrap passwordless sudo";
        public string DeployDescription => "Granting the deploy user temporary NOPASSWD sudo so provisioning steps can run";
        public string DeployPhaseId => "Sub_PasswordlessSudoBootstrap";
        public string DeployIconName => "Key24";
        public string? DeployCompletionInfo => null;
        public int DeployOrder => 45;

        public bool IsVisibleFor(GalleryItem? item)
            => item != null && item.HasTag(OmarchyTag);
    }
}
