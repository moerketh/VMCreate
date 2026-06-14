using System.Linq;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Checks and re-arms the Windows evaluation license on a dev-environment VM.
    ///
    /// Windows 11 Enterprise Evaluation VMs have a limited activation grace period.
    /// The pre-boot Unattend.xml already runs a FirstLogonCommand that attempts rearm,
    /// but the grace period may already be exhausted or the rearm may fail silently.
    /// This post-boot step detects an inactive license and attempts a second rearm
    /// via the SoftwareLicensingService WMI provider (no WSH pop-ups).
    ///
    /// If rearm succeeds, Windows will eventually need a reboot for the license to
    /// fully take effect, but we do NOT force a reboot here — the post-boot
    /// customization pipeline would be interrupted.  The user can reboot manually
    /// after deployment if the "Windows not activated" watermark appears.
    ///
    /// NOTE: The SoftwareLicensingProduct query sometimes writes the informational
    /// message "The security processor reported that the trusted data store was rearmed."
    /// to the error stream.  We treat this as success, not failure.
    /// </summary>
    public class FlareVmLicenseRearmStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "Rearm Windows License";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Windows;
        public int Order => 50; // Immediately after StabilizationStep (25)

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => IsVisibleFor(item);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Checking Windows license status on VM {VMName}...", shell.VmName);

            string licenseStatus = null;
            string licenseStatusTrimmed = null;
            try
            {
                licenseStatus = await shell.RunCommandAsync(
                    "Get-CimInstance SoftwareLicensingProduct -Filter \"Name like 'Windows%'\" | " +
                    "Where-Object { $_.PartialProductKey } | " +
                    "Select-Object -First 1 -ExpandProperty LicenseStatus", ct);
                licenseStatusTrimmed = licenseStatus?.Trim();
            }
            catch (Exception ex)
            {
                // The licensing subsystem sometimes writes success messages to stderr.
                // "The security processor reported that the trusted data store was rearmed."
                if (ex.Message.Contains("rearmed", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation(
                        "License rearm success detected on VM {VMName}: {Message}",
                        shell.VmName, ex.Message);
                    licenseStatusTrimmed = "1"; // treat as licensed
                }
                else
                {
                    logger.LogWarning(
                        ex, "License status query failed on VM {VMName}", shell.VmName);
                }
            }

            logger.LogInformation(
                "License status on VM {VMName}: {Status} (1=Licensed, 5=Expired)",
                shell.VmName, licenseStatusTrimmed);

            if (licenseStatusTrimmed == "1")
            {
                logger.LogInformation(
                    "Windows license is active on VM {VMName}. No rearm needed.",
                    shell.VmName);
                return;
            }

            logger.LogWarning(
                "License not active on VM {VMName} (status={Status}). Attempting rearm...",
                shell.VmName, licenseStatusTrimmed);

            string rearmResult = null;
            try
            {
                rearmResult = await shell.RunCommandAsync(
                    "$svc = Get-WmiObject -Query 'SELECT * FROM SoftwareLicensingService'; " +
                    "$r = $svc.RearmWindows(); " +
                    "Write-Host \"Rearm result: $r\"", ct);
                logger.LogInformation(
                    "Rearm result on VM {VMName}: {Result}",
                    shell.VmName, rearmResult?.Trim());
            }
            catch (Exception ex)
            {
                // "The security processor reported that the trusted data store was rearmed."
                // is actually a success message written to stderr by the licensing subsystem.
                if (ex.Message.Contains("rearmed", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation(
                        "License rearm succeeded on VM {VMName}: {Message}",
                        shell.VmName, ex.Message);
                }
                else
                {
                    logger.LogWarning(
                        ex, "License rearm attempt failed on VM {VMName}", shell.VmName);
                }
            }

            // We deliberately do NOT reboot here.  A reboot would interrupt the post-boot
            // customization pipeline (Defender removal, FLARE install, etc.).  The evaluation
            // license will be valid after the next natural reboot.
            logger.LogInformation(
                "License rearm complete on VM {VMName}. A reboot will be needed later for the license to fully activate.",
                shell.VmName);
        }

        // ── IConfigurableCustomizationStep (UI metadata) ────────────────
        public string CardTitle => "Rearm Windows License";
        public string CardDescription => "Check and re-arm the Windows evaluation license. Required for dev-environment VMs that may have an expired grace period.";
        public string Label => "Rearm Windows License";
        public string Tooltip => "Attempts to re-activate the Windows evaluation license using the SoftwareLicensingService. Does not force a reboot — the license takes effect after the next restart.";
        public bool DefaultEnabled => true;
        public bool IsOptional => false;

        // ── IDistributionOptionMetadata (deploy-phase UI) ───────────────
        public string DeployTitle => "Rearm Windows License";
        public string DeployDescription => "Rearming the Windows evaluation license";
        public string DeployPhaseId => "Sub_LicenseRearm";
        public string DeployIconName => "Key24";
        public int DeployOrder => 50;
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem item)
            => string.Equals(item?.Name, "FLARE VM", StringComparison.OrdinalIgnoreCase);
    }
}
