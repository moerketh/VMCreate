using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Verifies that Windows Defender was disabled offline before the VM's first boot.
    ///
    /// The actual disable happens in <see cref="UnattendInjector.DisableWindowsDefenderOffline"/>
    /// which loads the offline SOFTWARE and SYSTEM registry hives while the VHDX is mounted
    /// on the host. This runs before the VM ever boots, so WdFilter/WdBoot cannot block
    /// the changes. This step simply confirms the offline changes took effect.
    ///
    /// This step is required before installing FLARE VM because Windows Defender
    /// interferes with malware analysis tools and FLARE VM installation.
    /// </summary>
    public class FlareVmDisableDefenderStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        public FlareVmDisableDefenderStep()
        {
        }

        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "Remove Windows Defender";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Windows;
        public int Order => 100;

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => IsVisibleFor(item);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Verifying Windows Defender is disabled on VM {VMName}", shell.VmName);

            // Give Windows a few seconds to finish service initialization after first boot
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            await VerifyDefenderDisabledAsync(shell, logger, ct);

            logger.LogInformation("Windows Defender verification completed on VM {VMName}", shell.VmName);
        }

        /// <summary>
        /// Verifies (and remediates if needed) that Windows Defender is disabled after
        /// the offline registry modifications.  The Microsoft dev VHDX uses kernel-protected
        /// keys that cannot always be written offline, so we fall back to live remediation
        /// via reg.exe (which bypasses the PowerShell registry provider) and gpupdate.
        /// </summary>
        private async Task VerifyDefenderDisabledAsync(
            IGuestShell shell, ILogger logger, CancellationToken ct)
        {
            var errors = new List<string>();

            // ── 1. Check if Defender service is disabled ──
            string startType = await shell.RunCommandAsync(
                "(Get-Service WinDefend -ErrorAction SilentlyContinue).StartType", ct);
            string startTypeTrimmed = startType?.Trim();
            logger.LogInformation("WinDefend start type on VM {VMName}: {StartType}", shell.VmName, startTypeTrimmed);
            bool startTypeIsDisabled = string.Equals(startTypeTrimmed, "Disabled", StringComparison.OrdinalIgnoreCase)
                || startTypeTrimmed == "4";

            // ── 2. Check if Defender is actually running ──
            string svcResult = await shell.RunCommandAsync(
                "(Get-Service WinDefend -ErrorAction SilentlyContinue).Status", ct);
            string svcStatusTrimmed = svcResult?.Trim();
            logger.LogInformation("WinDefend service status on VM {VMName}: {Status}", shell.VmName, svcStatusTrimmed);
            bool svcStatusIsStopped = string.Equals(svcStatusTrimmed, "Stopped", StringComparison.OrdinalIgnoreCase)
                || svcStatusTrimmed == "1";

            // ── 3. If service is already disabled/stopped, the offline hive edits worked ──
            if (startTypeIsDisabled && svcStatusIsStopped)
            {
                logger.LogInformation("WinDefend service is disabled/stopped — offline registry modification succeeded.");

                // Still try to set wuauserv if not disabled (it may not have been in earlier builds)
                await EnsureServiceDisabledAsync(shell, "wuauserv", logger, ct);

                // Verify SmartScreen
                await VerifySmartScreenAsync(shell, logger, errors, ct);

                if (errors.Count > 0)
                {
                    string errorMessage = $"Windows Defender was not fully disabled on VM {shell.VmName}:\n" +
                        string.Join("\n", errors.Select(e => $"  - {e}"));
                    logger.LogError(errorMessage);
                    throw new InvalidOperationException(errorMessage);
                }

                logger.LogInformation("Windows Defender successfully disabled on VM {VMName}", shell.VmName);
                return;
            }

            // ── 4. Remediation: Service is still running — try live disable ──
            logger.LogWarning("WinDefend is not disabled ({StartType}/{Status}). Attempting live remediation...",
                startTypeTrimmed, svcStatusTrimmed);

            // Try stopping and disabling the service
            await shell.RunCommandAsync("Stop-Service WinDefend -Force -ErrorAction SilentlyContinue; Set-Service WinDefend -StartupType Disabled -ErrorAction SilentlyContinue", ct);

            // Try live registry remediation via reg.exe (bypasses kernel protection)
            await shell.RunCommandAsync(
                "reg add 'HKLM\\SOFTWARE\\Microsoft\\Windows Defender\\Features' /v TamperProtection /t REG_DWORD /d 0 /f", ct);
            await shell.RunCommandAsync(
                "reg add 'HKLM\\SOFTWARE\\Microsoft\\Windows Defender\\Features' /v DisableAntiVirus /t REG_DWORD /d 1 /f", ct);
            await shell.RunCommandAsync(
                "reg add 'HKLM\\SOFTWARE\\Microsoft\\Windows Defender\\Features' /v DisableAntiSpyware /t REG_DWORD /d 1 /f", ct);
            await shell.RunCommandAsync(
                "reg add 'HKLM\\SOFTWARE\\Microsoft\\Windows Defender\\Real-Time Protection' /v DisableRealtimeMonitoring /t REG_DWORD /d 1 /f", ct);
            await shell.RunCommandAsync(
                "reg add 'HKLM\\SOFTWARE\\Microsoft\\Windows Defender\\Real-Time Protection' /v DisableBehaviorMonitoring /t REG_DWORD /d 1 /f", ct);
            await shell.RunCommandAsync(
                "reg add 'HKLM\\SOFTWARE\\Microsoft\\Windows Defender\\Real-Time Protection' /v DisableOnAccessProtection /t REG_DWORD /d 1 /f", ct);

            // Force group policy update to apply the policy keys we set offline
            await shell.RunCommandAsync("gpupdate /force /wait:30 2>$null", ct);

            // Re-check after remediation
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            string startTypeAfter = await shell.RunCommandAsync(
                "(Get-Service WinDefend -ErrorAction SilentlyContinue).StartType", ct);
            string statusAfter = await shell.RunCommandAsync(
                "(Get-Service WinDefend -ErrorAction SilentlyContinue).Status", ct);

            bool startTypeOk = string.Equals(startTypeAfter?.Trim(), "Disabled", StringComparison.OrdinalIgnoreCase)
                || startTypeAfter?.Trim() == "4";
            bool statusOk = string.Equals(statusAfter?.Trim(), "Stopped", StringComparison.OrdinalIgnoreCase)
                || statusAfter?.Trim() == "1";

            if (!startTypeOk)
            {
                errors.Add($"WinDefend start type is {startTypeAfter?.Trim()} after remediation (expected Disabled/4)");
            }
            if (!statusOk)
            {
                errors.Add($"WinDefend status is {statusAfter?.Trim()} after remediation (expected Stopped/1)");
            }

            // ── 5. Check Windows Update ──
            await EnsureServiceDisabledAsync(shell, "wuauserv", logger, ct);

            // ── 6. Check SmartScreen ──
            await VerifySmartScreenAsync(shell, logger, errors, ct);

            if (errors.Count > 0)
            {
                string errorMessage = $"Windows Defender was not fully disabled on VM {shell.VmName}:\n" +
                    string.Join("\n", errors.Select(e => $"  - {e}"));
                logger.LogError(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }

            logger.LogInformation("Windows Defender successfully disabled on VM {VMName} after remediation", shell.VmName);
        }

        private async Task EnsureServiceDisabledAsync(IGuestShell shell, string serviceName, ILogger logger, CancellationToken ct)
        {
            string startType = await shell.RunCommandAsync(
                $"(Get-Service {serviceName} -ErrorAction SilentlyContinue).StartType", ct);
            string startTypeTrimmed = startType?.Trim();
            bool isDisabled = string.Equals(startTypeTrimmed, "Disabled", StringComparison.OrdinalIgnoreCase)
                || startTypeTrimmed == "4";
            if (!isDisabled)
            {
                logger.LogWarning("{Service} start type is {StartType} (expected Disabled/4). Disabling live...",
                    serviceName, startTypeTrimmed);
                await shell.RunCommandAsync(
                    $"Stop-Service {serviceName} -Force -ErrorAction SilentlyContinue; Set-Service {serviceName} -StartupType Disabled -ErrorAction SilentlyContinue", ct);
            }
            else
            {
                logger.LogInformation("{Service} is already disabled.", serviceName);
            }
        }

        private async Task VerifySmartScreenAsync(IGuestShell shell, ILogger logger, List<string> errors, CancellationToken ct)
        {
            string ssResult = await shell.RunCommandAsync(
                "(Get-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer' -Name 'SmartScreenEnabled' -ErrorAction SilentlyContinue).SmartScreenEnabled", ct);
            string ssValue = ssResult?.Trim();
            logger.LogInformation("SmartScreenEnabled value on VM {VMName}: {Value}", shell.VmName, ssValue);
            bool smartScreenDisabled = string.Equals(ssValue, "Off", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(ssValue);
            if (!smartScreenDisabled)
            {
                errors.Add($"SmartScreen is {ssValue} (expected Off)");
            }
        }

        // ── IConfigurableCustomizationStep (UI metadata) ────────────────
        public string CardTitle => "Remove Windows Defender";
        public string CardDescription => "Remove Windows Defender by modifying the offline registry before first boot. Required for FLARE VM installation.";
        public string Label => "Remove Windows Defender (required)";
        public string Tooltip => "Disables Windows Defender, Tamper Protection, and related security services offline before the VM boots. Verified after first boot.";
        public bool DefaultEnabled => true;
        public bool IsOptional => false;

        // ── IDistributionOptionMetadata (deploy-phase UI) ───────────────
        public string DeployTitle => "Remove Windows Defender";
        public string DeployDescription => "Removing Windows Defender and related security components";
        public string DeployPhaseId => "Sub_RemoveDefender";
        public string DeployIconName => "Shield24";
        public int DeployOrder => 100;
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem item)
            => string.Equals(item?.Name, "FLARE VM", StringComparison.OrdinalIgnoreCase);
    }
}
