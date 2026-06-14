using System.Linq;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Disables Windows Update service and configures registry policies to prevent
    /// automatic updates on a Windows VM. This is required for FLARE VM because:
    ///   1. Windows Updates can re-enable Windows Defender
    ///   2. Updates can break installed malware analysis tools
    ///   3. A malware analysis environment should remain stable and unchanged
    /// </summary>
    public class FlareVmDisableUpdatesStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "Disable Windows Updates";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Windows;
        public int Order => 40;

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => IsVisibleFor(item);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Disabling Windows Updates on VM {VMName}", shell.VmName);

            // Run disable commands directly via PowerShell Direct.  Previous iterations used
            // a scheduled task with RunLevel Highest, but the task-creation/polling overhead
            // added ~8-10 minutes and frequently timed out.  PowerShell Direct sessions for
            // the local "flare" administrator account have sufficient privilege to stop
            // services and write to HKLM\Policies, so direct execution is much faster.

            string disableScript = @"
                # Stop and disable Windows Update service
                Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue
                Set-Service -Name wuauserv -StartupType Disabled -ErrorAction SilentlyContinue

                # Disable Windows Update Medic Service (WaaSMedicSvc)
                Stop-Service -Name WaaSMedicSvc -Force -ErrorAction SilentlyContinue
                Set-Service -Name WaaSMedicSvc -StartupType Disabled -ErrorAction SilentlyContinue

                # Registry policies to block Windows Update
                $wuKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
                $auKey = Join-Path $wuKey 'AU'
                if (-not (Test-Path $wuKey)) { New-Item -Path $wuKey -Force | Out-Null }
                if (-not (Test-Path $auKey)) { New-Item -Path $auKey -Force | Out-Null }
                Set-ItemProperty -Path $auKey -Name 'NoAutoUpdate' -Value 1 -Type DWord -Force
                Set-ItemProperty -Path $wuKey -Name 'DoNotConnectToWindowsUpdateInternetLocations' -Value 1 -Type DWord -Force
                Set-ItemProperty -Path $wuKey -Name 'SetAutoDownloadMinor' -Value 0 -Type DWord -Force

                # Disable Windows Update Medic registry settings
                $medicKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WaaSMedic'
                if (-not (Test-Path $medicKey)) { New-Item -Path $medicKey -Force | Out-Null }
                Set-ItemProperty -Path $medicKey -Name 'AllowAutoUpdate' -Value 0 -Type DWord -Force
                Set-ItemProperty -Path $medicKey -Name 'AllowAutoHealthCheck' -Value 0 -Type DWord -Force

                # Disable Update Orchestrator scheduled tasks
                schtasks /Change /TN '\Microsoft\Windows\UpdateOrchestrator\Schedule Scan' /Disable 2>$null
                schtasks /Change /TN '\Microsoft\Windows\UpdateOrchestrator\Schedule Scan Static' /Disable 2>$null
                schtasks /Change /TN '\Microsoft\Windows\UpdateOrchestrator\USO_UxBroker' /Disable 2>$null

                Write-Host 'Windows Update disabled.'
            ";

            await shell.RunCommandAsync(disableScript, ct);
            logger.LogInformation("Windows Update disable commands executed on VM {VMName}", shell.VmName);

            // ── Verify Windows Update is actually disabled ──
            string wuStartType = await shell.RunCommandAsync(
                "(Get-Service wuauserv -ErrorAction SilentlyContinue).StartType", ct);
            string wuStartTypeTrimmed = wuStartType?.Trim();
            logger.LogInformation("wuauserv start type on VM {VMName}: {StartType}", shell.VmName, wuStartTypeTrimmed);
            bool wuIsDisabled = string.Equals(wuStartTypeTrimmed, "Disabled", StringComparison.OrdinalIgnoreCase)
                || wuStartTypeTrimmed == "4";
            if (!wuIsDisabled)
            {
                logger.LogWarning("wuauserv start type is {StartType} on VM {VMName} (expected Disabled/4). Attempting live disable...", wuStartTypeTrimmed, shell.VmName);
                await shell.RunCommandAsync(
                    "Stop-Service wuauserv -Force -ErrorAction SilentlyContinue; Set-Service wuauserv -StartupType Disabled -ErrorAction SilentlyContinue", ct);
            }

            string wuStatus = await shell.RunCommandAsync(
                "(Get-Service wuauserv -ErrorAction SilentlyContinue).Status", ct);
            string wuStatusTrimmed = wuStatus?.Trim();
            logger.LogInformation("wuauserv status on VM {VMName}: {Status}", shell.VmName, wuStatusTrimmed);
            bool wuIsStopped = string.Equals(wuStatusTrimmed, "Stopped", StringComparison.OrdinalIgnoreCase)
                || wuStatusTrimmed == "1";
            if (!wuIsStopped)
            {
                logger.LogWarning("wuauserv is still running on VM {VMName} (status={Status}). Stopping...", shell.VmName, wuStatusTrimmed);
                await shell.RunCommandAsync("Stop-Service wuauserv -Force -ErrorAction SilentlyContinue", ct);
            }

            // Also verify WaaSMedicSvc is disabled
            string medicStartType = await shell.RunCommandAsync(
                "(Get-Service WaaSMedicSvc -ErrorAction SilentlyContinue).StartType", ct);
            string medicStartTypeTrimmed = medicStartType?.Trim();
            bool medicIsDisabled = string.Equals(medicStartTypeTrimmed, "Disabled", StringComparison.OrdinalIgnoreCase)
                || medicStartTypeTrimmed == "4";
            if (!medicIsDisabled)
            {
                logger.LogWarning("WaaSMedicSvc start type is {StartType} on VM {VMName}. Disabling...", medicStartTypeTrimmed, shell.VmName);
                await shell.RunCommandAsync(
                    "Stop-Service WaaSMedicSvc -Force -ErrorAction SilentlyContinue; Set-Service WaaSMedicSvc -StartupType Disabled -ErrorAction SilentlyContinue", ct);
            }

            logger.LogInformation("Windows Updates disabled on VM {VMName}", shell.VmName);
        }

        // ── IConfigurableCustomizationStep (UI metadata) ────────────────
        public string CardTitle => "Disable Windows Updates";
        public string CardDescription => "Permanently disable Windows Update service and set registry policies to prevent automatic updates.";
        public string Label => "Disable Windows Updates (recommended)";
        public string Tooltip => "Disables the Windows Update service and configures registry policies to prevent automatic updates. FLARE VM requires this to avoid Defender reactivation and tool breakage.";
        public bool DefaultEnabled => true;
        public bool IsOptional => false;

        // ── IDistributionOptionMetadata (deploy-phase UI) ───────────────
        public string DeployTitle => "Disable Windows Updates";
        public string DeployDescription => "Disabling automatic Windows updates";
        public string DeployPhaseId => "Sub_DisableUpdates";
        public string DeployIconName => "LockClosed24";
        public int DeployOrder => 40;
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem item)
            => string.Equals(item?.Name, "FLARE VM", StringComparison.OrdinalIgnoreCase);
    }
}