using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Removes or disables noisy scheduled tasks that interfere with FLARE VM installation
    /// or create unnecessary log noise during post-boot customization.
    /// Runs early in the post-boot pipeline before other steps.
    /// </summary>
    public class FlareVmCleanupTasksStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        public string Name => "Cleanup Scheduled Tasks";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Windows;
        public int Order => 35; // After stabilization (25), before Defender disable (100)

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => IsVisibleFor(item);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Cleaning up noisy scheduled tasks on VM {VMName}...", shell.VmName);

            // Wrap the cleanup in a transcript so we can see exactly what ran and what tasks were found.
            // The transcript is written to a temp file, then its content is returned as the command output.
            string transcriptPath = @"C:\Users\flare\Desktop\task-cleanup-transcript.log";
            string transcriptQuoted = transcriptPath.Replace("'", "''");

            string cleanupScript =
                "$transcript = '" + transcriptQuoted + "'\n" +
                "Start-Transcript -Path $transcript -Force | Out-Null\n" +
                "\n" +
                "# --- Edge Update tasks ---\n" +
                "Write-Host '--- Searching for Edge-related tasks ---'\n" +
                "$edgeTasks = Get-ScheduledTask | Where-Object { $_.TaskName -like '*Edge*' -or $_.TaskName -like '*MicrosoftEdge*' }\n" +
                "Write-Host ('Found ' + $edgeTasks.Count + ' Edge-related task(s)')\n" +
                "if ($edgeTasks) {\n" +
                "    $edgeTasks | ForEach-Object { Write-Host ('  Task: ' + $_.TaskPath + $_.TaskName + ' State=' + $_.State) }\n" +
                "    $edgeTasks | Disable-ScheduledTask -ErrorAction SilentlyContinue\n" +
                "    Write-Host 'Edge tasks disabled.'\n" +
                "} else {\n" +
                "    Write-Host 'No Edge-related tasks found.'\n" +
                "}\n" +
                "\n" +
                "# --- Windows Update tasks ---\n" +
                "Write-Host '--- Searching for Windows Update tasks ---'\n" +
                "$wuTasks = Get-ScheduledTask | Where-Object { $_.TaskPath -like '*WindowsUpdate*' -or $_.TaskPath -like '*UpdateOrchestrator*' -or $_.TaskName -like '*USO*' -or $_.TaskName -like '*Update*' }\n" +
                "Write-Host ('Found ' + $wuTasks.Count + ' Windows Update task(s)')\n" +
                "if ($wuTasks) {\n" +
                "    $wuTasks | ForEach-Object { Write-Host ('  Task: ' + $_.TaskPath + $_.TaskName + ' State=' + $_.State) }\n" +
                "    $wuTasks | Disable-ScheduledTask -ErrorAction SilentlyContinue\n" +
                "    Write-Host 'Windows Update tasks disabled.'\n" +
                "} else {\n" +
                "    Write-Host 'No Windows Update tasks found.'\n" +
                "}\n" +
                "\n" +
                "# --- Office update tasks ---\n" +
                "Write-Host '--- Searching for Office tasks ---'\n" +
                "$officeTasks = Get-ScheduledTask | Where-Object { $_.TaskName -like '*Office*' }\n" +
                "Write-Host ('Found ' + $officeTasks.Count + ' Office task(s)')\n" +
                "if ($officeTasks) {\n" +
                "    $officeTasks | ForEach-Object { Write-Host ('  Task: ' + $_.TaskPath + $_.TaskName + ' State=' + $_.State) }\n" +
                "    $officeTasks | Disable-ScheduledTask -ErrorAction SilentlyContinue\n" +
                "    Write-Host 'Office tasks disabled.'\n" +
                "} else {\n" +
                "    Write-Host 'No Office tasks found.'\n" +
                "}\n" +
                "\n" +
                "Stop-Transcript | Out-Null\n" +
                "# Return the transcript content so the host can log it\n" +
                "Get-Content -Path $transcript -Raw";

            string result = await shell.RunCommandAsync(cleanupScript, ct);

            // Log the full transcript back into our application log
            if (!string.IsNullOrWhiteSpace(result))
            {
                logger.LogDebug("Task cleanup transcript for VM {VMName}:\n{Transcript}", shell.VmName, result);
            }

            logger.LogInformation("Scheduled task cleanup completed on VM {VMName}", shell.VmName);
        }

        public string CardTitle => "Cleanup Scheduled Tasks";
        public string CardDescription => "Disable noisy scheduled tasks (Edge Update, Windows Update, Office) to reduce log noise during FLARE VM installation.";
        public string Label => "Disable noisy scheduled tasks";
        public string Tooltip => "Disables scheduled tasks that create unnecessary log noise or may interfere with the FLARE VM installation process.";
        public bool DefaultEnabled => true;
        public bool IsOptional => false;

        // ── IDistributionOptionMetadata (deploy-phase UI) ───────────────
        public string DeployTitle => "Disable Noisy Scheduled Tasks";
        public string DeployDescription => "Disabling noisy scheduled tasks (Edge, Windows Update)";
        public string DeployPhaseId => "Sub_CleanupTasks";
        public string DeployIconName => "TimerOff24";
        public int DeployOrder => 35;
        public string? DeployCompletionInfo => null;

        public bool IsVisibleFor(GalleryItem item)
            => string.Equals(item?.Name, "FLARE VM", StringComparison.OrdinalIgnoreCase);
    }
}
