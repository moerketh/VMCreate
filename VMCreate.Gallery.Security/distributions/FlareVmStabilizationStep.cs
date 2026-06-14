using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Waits for the VM to finish OOBE and reach a usable desktop state before running
    /// other customization steps.
    ///
    /// When a Windows dev-environment VM first boots, it shows "Getting your machine ready"
    /// and other OOBE/ESP screens.  PowerShell Direct may connect during this phase, but
    /// the desktop shell (explorer) is not yet ready.  This step probes for explorer
    /// and the desktop instead of blindly sleeping.
    ///
    /// The original 2-minute blind delay was reduced to 45 s, but even that can be too
    /// short or too long depending on the host.  Probing is more accurate.
    /// </summary>
    public class FlareVmStabilizationStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "Stabilize VM";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Windows;
        public int Order => 25;

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => IsVisibleFor(item);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            logger.LogInformation(
                "Waiting for VM {VMName} to finish OOBE and reach the desktop...", shell.VmName);

            // Windows dev-evaluation VMs show "Getting your machine ready" for 1-2 minutes
            // after the first PowerShell Direct connection.  Explorer may already be running
            // during this phase, so probing alone returns too early.  We perform a host-side
            // blind wait first, then probe to confirm the desktop is actually usable.
            logger.LogInformation(
                "VM {VMName}: performing initial 2-minute OOBE wait...", shell.VmName);
            await Task.Delay(TimeSpan.FromMinutes(2), ct);

            // After the blind wait, verify the desktop is actually reachable.
            // If it isn't (rare), we poll briefly before giving up.
            bool desktopReady = false;
            for (int attempt = 1; attempt <= 12; attempt++)  // 12 × 10 s = 2 min extra
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    string explorerPid = await shell.RunCommandAsync(
                        "(Get-Process -Name explorer -ErrorAction SilentlyContinue).Id", ct);

                    if (!string.IsNullOrWhiteSpace(explorerPid))
                    {
                        string desktopPath = await shell.RunCommandAsync(
                            "Test-Path (Join-Path $env:USERPROFILE 'Desktop')", ct);

                        if (desktopPath?.Trim() == "True")
                        {
                            desktopReady = true;
                            logger.LogInformation(
                                "VM {VMName} desktop confirmed ready after {TotalSeconds}s (explorer PID={Pid}).",
                                shell.VmName, 120 + attempt * 10, explorerPid.Trim());
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        "Desktop probe attempt {Attempt} failed on VM {VMName}: {Message}",
                        attempt, shell.VmName, ex.Message);
                }

                logger.LogDebug(
                    "VM {VMName} desktop not ready yet (attempt {Attempt}/{Max}), waiting 10s...",
                    shell.VmName, attempt, 12);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }

            if (!desktopReady)
            {
                logger.LogWarning(
                    "VM {VMName} desktop was not confirmed ready after {Seconds}s. " +
                    "Proceeding anyway — subsequent steps will retry if needed.",
                    shell.VmName, 120 + 120);
            }

            // Ensure the shell session is fresh after the wait
            logger.LogInformation("Reconnecting to VM {VMName} after stabilization...", shell.VmName);
            await shell.WaitForReadyAsync(ct);
            logger.LogInformation("VM {VMName} is stabilized and ready for customization", shell.VmName);
        }

        // ── IConfigurableCustomizationStep (UI metadata) ────────────────
        public string CardTitle => "Stabilize VM";
        public string CardDescription => "Wait for the VM to finish OOBE and reach the desktop before running customization steps.";
        public string Label => "Stabilize VM (required)";
        public string Tooltip => "Probes for the desktop shell (explorer) to confirm Windows has finished first-boot setup. Ensures subsequent steps run reliably.";
        public bool DefaultEnabled => true;
        public bool IsOptional => false;

        // ── IDistributionOptionMetadata (deploy-phase UI) ───────────────
        public string DeployTitle => "Stabilize VM";
        public string DeployDescription => "Waiting for Windows services to fully initialize";
        public string DeployPhaseId => "Sub_StabilizeVm";
        public string DeployIconName => "Hourglass24";
        public int DeployOrder => 25;
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem item)
            => string.Equals(item?.Name, "FLARE VM", StringComparison.OrdinalIgnoreCase);
    }
}
