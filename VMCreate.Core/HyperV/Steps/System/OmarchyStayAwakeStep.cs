using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Switches Omarchy's idle handling to "Stay Awake" for the deploy user so
    /// the VM console never blanks to the screensaver or drops to the lock
    /// screen. A screen lock inside a VM only gets in the way: the host
    /// session already locks, and a locked Hyprland blanks screencopy, so an
    /// RDP client then shows a black desktop.
    /// <para>
    /// Uses Omarchy's own switch rather than editing its idle timeouts: the
    /// quickshell idle service disables both screensaver and lock while
    /// <c>~/.local/state/omarchy/indicators/stay-awake</c> exists, and watches
    /// that directory, so a running session follows immediately. The bar's
    /// idle indicator shows the state and "Allow Idle Lock &amp; Screensaver"
    /// reverts it. <c>omarchy-toggle-idle stay-awake</c> is preferred; the
    /// direct <c>touch</c> covers an image without that helper.
    /// </para>
    /// <para>
    /// Gated on the <c>omarchy</c> tag and independent of the RDP backend.
    /// Order 237 runs inside the post-boot SSH window as the deploy user, ahead
    /// of the graphical session start (239). Best-effort: a miss only leaves
    /// Omarchy's stock idle behaviour in place.
    /// </para>
    /// </summary>
    public class OmarchyStayAwakeStep : ICustomizationStep, IStepDisplayInfo
    {
        internal const string StateFile = "$HOME/.local/state/omarchy/indicators/stay-awake";

        public string Name => "Disable Omarchy idle lock and screensaver";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 237;
        public string? ProgressPhaseId => null;

        public string DeployTitle => "Keep Display Awake";
        public string DeployDescription => "Disabling the Omarchy idle lock and screensaver";
        public string DeployIconName => "Desktop24";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => item != null && item.HasTag(EnablePasswordlessSudoStep.OmarchyTag);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                string result = await shell.RunCommandAsync(
                    "{ command -v omarchy-toggle-idle >/dev/null 2>&1 && omarchy-toggle-idle stay-awake >/dev/null 2>&1; } " +
                    $"|| {{ mkdir -p \"$(dirname \"{StateFile}\")\" && touch \"{StateFile}\"; }}; " +
                    $"[ -f \"{StateFile}\" ] && echo STAY_AWAKE_RESULT=ok || echo STAY_AWAKE_RESULT=failed", ct);

                if (result.Contains("STAY_AWAKE_RESULT=ok"))
                    logger.LogInformation("Omarchy idle lock and screensaver disabled (Stay Awake) on VM {VMName}.", shell.VmName);
                else
                    logger.LogWarning("Could not enable Omarchy Stay Awake on VM {VMName} — the console keeps Omarchy's stock idle lock: {Result}", shell.VmName, result.Trim());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Omarchy Stay Awake failed on VM {VMName} — the deployment continues with the stock idle lock: {Message}",
                    shell.VmName, ex.Message);
            }
        }
    }
}
