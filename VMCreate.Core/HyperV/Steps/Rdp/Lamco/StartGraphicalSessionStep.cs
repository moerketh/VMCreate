using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Activates the graphical session on a freshly provisioned Lamco VM
    /// <b>inside the deployment</b> — the step that removes the manual
    /// post-deploy reboot.
    /// <para>
    /// Why it exists: <see cref="InstallLamcoRdpStep"/> (order 235) installs
    /// the server and its user units, and <see cref="EnableGraphicalAutologinStep"/>
    /// (order 238) writes the DM autologin configuration — but both only take
    /// effect at the <i>next</i> display-manager start. On a fresh deployment
    /// the VM sits at the DM greeter: <c>graphical-session.target</c> is
    /// inactive, the lamco units (server, consent grant, idle inhibitor —
    /// all <c>WantedBy=graphical-session.target</c>) are armed but dead, and
    /// the install step's readiness gate correctly reports "deferred".
    /// Every prior E2E run ended there — "deployed but requires one manual
    /// reboot" (TEST_20260910165003, TEST_20260910195849).
    /// </para>
    /// <para>
    /// The step restarts the display manager over SSH when no session is
    /// active. The DM re-reads its (now autologin-configured) state, logs
    /// the desktop user into the Wayland session, and
    /// <c>graphical-session.target</c> pulls in the lamco units — the
    /// DM-scoped equivalent of the reboot, without one. It then polls for
    /// the same readiness verdicts the install script's gate uses:
    /// <c>ready</c> (listeners bound), <c>consent</c> (one-time portal
    /// dialog on the console — expected on first boot, not a failure), or
    /// <c>timeout</c> (degraded).
    /// </para>
    /// <para>
    /// Runs at Order 239, immediately after
    /// <see cref="EnableGraphicalAutologinStep"/> (238) and before the xrdp
    /// block (240-270, skipped for Lamco). Gated to
    /// <see cref="RdpBackend.Lamco"/> + Lamco-capable gallery items — the
    /// xrdp path creates its session at connect time and must not have its
    /// DM restarted out from under the greeter.
    /// </para>
    /// <para>
    /// Result contract of <c>start_graphical_session.sh</c>: the terminal
    /// line <c>SESSION_START_RESULT=ok|degraded|consent</c>. Hard failures
    /// exit non-zero (RunCommandAsync throws); a missing line on a
    /// zero-exit run is treated as failed — a swallowed session activation
    /// must never report success.
    /// </para>
    /// </summary>
    public class StartGraphicalSessionStep : ICustomizationStep
    {
        public string Name => "Start Graphical Session";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 239;
        public string? ProgressPhaseId => "Sub_StartGraphicalSession";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => customizations?.RdpBackend == RdpBackend.Lamco && item.SupportsLamco();

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation(
                "Activating graphical session on VM {VMName} (no-reboot completion for Lamco)...",
                shell.VmName);

            string script = ScriptResourceLoader.Load("start_graphical_session.sh");

            // Same shared autologin-user resolver contract as
            // InstallLamcoRdpStep/EnableGraphicalAutologinStep: the gallery
            // item's InitialUsername is authoritative; the script has a
            // /etc/passwd fallback and hard-fails on no user. The value is
            // validated before substitution into the root-run script.
            if (!string.IsNullOrWhiteSpace(item?.InitialUsername))
            {
                string initialUsername = item.InitialUsername!;
                if (!UsernameValidator.IsValidLinuxUsername(initialUsername))
                {
                    throw new InvalidOperationException(
                        $"Gallery item InitialUsername '{initialUsername}' is not a valid Linux username. " +
                        "Refusing to substitute it into a root-run script.");
                }
                script = script.Replace("__AUTOLOGIN_USER__", initialUsername);
            }
            else
            {
                logger.LogWarning(
                    "Gallery item has no InitialUsername for VM {VMName}; the session-start script will resolve the desktop user from /etc/passwd.",
                    shell.VmName);
            }

            // /tmp hardening: unpredictable GUID path + root:0700 before
            // execution — see InstallLamcoRdpStep for the TOCTOU rationale.
            string guestScript = $"/tmp/start_graphical_session_{Guid.NewGuid():N}.sh";
            await shell.CopyContentAsync(script, guestScript, ct);

            // The DM restart + Wayland session + portal grant attempt can
            // take ~60-90s on a 2-vCPU guest; the script's internal poll
            // budget is 120s, so allow 5 minutes end-to-end.
            string result = await shell.RunCommandAsync(
                $"sudo chown root:root {guestScript} && sudo chmod 0700 {guestScript} && sudo bash {guestScript} && sudo rm -f {guestScript}",
                TimeSpan.FromMinutes(5), ct);

            var (outcome, detail) = ParseResultLine(result);
            switch (outcome)
            {
                case SessionStartOutcome.Ok:
                    logger.LogInformation(
                        "Graphical session active on VM {VMName}: {Result}",
                        shell.VmName, result.Trim());
                    break;
                case SessionStartOutcome.Consent:
                    // Expected first-boot verdict: the deployment is complete
                    // and correct; the one-time portal consent dialog is on
                    // the VM console and one Allow click binds the listeners.
                    logger.LogInformation(
                        "Graphical session active on VM {VMName}; one-time portal consent dialog is on the VM console — " +
                        "click Allow once and the lamco listeners bind. {Result}",
                        shell.VmName, result.Trim());
                    break;
                case SessionStartOutcome.Degraded:
                    logger.LogWarning(
                        "Graphical session activation completed DEGRADED on VM {VMName}. The VM may still need one manual reboot: {Result}",
                        shell.VmName, detail);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Session activation on VM {shell.VmName} reported no SESSION_START_RESULT line. " +
                        "The script exited zero but did not confirm success — treating as failure.");
            }
        }

        private static (SessionStartOutcome outcome, string detail) ParseResultLine(string output)
        {
            // The terminal line is the LAST SESSION_START_RESULT= in the output.
            string? last = null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("SESSION_START_RESULT=", StringComparison.Ordinal))
                    last = trimmed;
            }
            if (last is null) return (SessionStartOutcome.Failed, output.Trim());
            var value = last["SESSION_START_RESULT=".Length..];
            if (value.Equals("ok", StringComparison.Ordinal))
                return (SessionStartOutcome.Ok, string.Empty);
            if (value.Equals("consent", StringComparison.Ordinal))
                return (SessionStartOutcome.Consent, string.Empty);
            if (value.Equals("degraded", StringComparison.Ordinal))
                return (SessionStartOutcome.Degraded, output.Trim());
            return (SessionStartOutcome.Failed, output.Trim());
        }

        private enum SessionStartOutcome
        {
            Ok,
            Consent,
            Degraded,
            Failed
        }
    }
}