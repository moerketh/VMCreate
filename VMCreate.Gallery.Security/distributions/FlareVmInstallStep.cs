using System.Linq;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Installs the FLARE VM toolkit on a Windows VM using Mandiant's install.ps1 script.
    ///
    /// FLARE VM is a collection of software installation scripts for Windows that
    /// allows you to easily setup and maintain a reverse engineering environment on a VM.
    /// It uses Chocolatey and Boxstarter to install 100+ malware analysis tools.
    ///
    /// Prerequisites (must be completed before this step):
    ///   - Windows Defender must be removed (FlareVmDisableDefenderStep)
    ///   - Windows Updates must be disabled (FlareVmDisableUpdatesStep)
    ///   - Noisy scheduled tasks must be disabled (FlareVmCleanupTasksStep)
    ///   - The VM must have internet access (Default Switch)
    ///
    /// The installation runs as the VM user (flare) via a scheduled task so that
    /// Boxstarter can set auto-login registry keys for the correct account.
    /// Output is captured to C:\Users\flare\Desktop\flare-install.log for diagnostics.
    /// The installation may involve multiple reboots handled by Boxstarter.
    /// </summary>
    public class FlareVmInstallStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "Install FLARE VM";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Windows;
        public int Order => 200;

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => IsVisibleFor(item)
               && customizations.DistributionOptions.Any(o => string.Equals(o.Name, Name, StringComparison.OrdinalIgnoreCase) && o.IsEnabled);

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Starting FLARE VM installation on VM {VMName}", shell.VmName);

            string password = item.InitialPassword ?? "flare";
            string username = item.InitialUsername ?? "flare";
            string taskName = "FLARE-VM-Install";
            string installPs1 = @"C:\Users\flare\Desktop\install.ps1";
            string logPath = @"C:\Users\flare\Desktop\flare-install.log";
            string errorLogPath = @"C:\Users\flare\Desktop\flare-install-errors.log";
            string wrapperPath = @"C:\Users\flare\Desktop\install-wrapper.ps1";

            // Sanitize for embedding in C# strings and PowerShell strings
            string installPs1Quoted = installPs1.Replace("'", "''");
            string passwordQuoted = password.Replace("'", "''");
            string usernameQuoted = username.Replace("'", "''");
            string logPathQuoted = logPath.Replace("'", "''");
            string errorLogQuoted = errorLogPath.Replace("'", "''");
            string wrapperQuoted = wrapperPath.Replace("'", "''");

            // 1. Download the FLARE VM install script
            logger.LogInformation("Downloading FLARE VM install script to Desktop...");
            string downloadScript =
                "$url = 'https://raw.githubusercontent.com/mandiant/flare-vm/main/install.ps1'\n" +
                "$out = '" + installPs1Quoted + "'\n" +
                "Write-Host 'Downloading FLARE VM install script...'\n" +
                "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12\n" +
                "Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing\n" +
                "Write-Host 'Download complete.'";
            await shell.RunCommandAsync(downloadScript, ct);

            // 2. Unblock and set execution policy (machine-wide so Boxstarter child processes inherit it)
            logger.LogInformation("Unblocking install script and setting execution policy...");
            string prepScript =
                "Unblock-File -Path '" + installPs1Quoted + "'\n" +
                "Set-ExecutionPolicy Unrestricted -Force -Scope LocalMachine\n" +
                "Write-Host 'Script ready.'";
            await shell.RunCommandAsync(prepScript, ct);

            // 3. Create a wrapper script that runs install.ps1 and captures output.
            //    This avoids complex quoting when passing arguments to a scheduled task.
            logger.LogInformation("Creating FLARE install wrapper script...");
            string createWrapper =
                "$wrapper = '" + wrapperQuoted + "'\n" +
                "$install = '" + installPs1Quoted + "'\n" +
                "$pass = '" + passwordQuoted + "'\n" +
                "$log = '" + logPathQuoted + "'\n" +
                "$errLog = '" + errorLogQuoted + "'\n" +
                "\n" +
                "$lines = @(\n" +
                "    \"# Wrapper for FLARE VM install.ps1\",\n" +
                "    \"param()\",\n" +
                "    \"Start-Transcript -Path '$errLog' -IncludeInvocationHeader -Force -ErrorAction SilentlyContinue\",\n" +
                "    \"Write-Host 'Starting FLARE VM installation...'\",\n" +
                "    \"& '$install' -password '$pass' -noWait -noGui -noChecks 2>&1 | Tee-Object -FilePath '$log'\",\n" +
                "    \"Write-Host 'FLARE VM installation completed.'\"\n" +
                ")\n" +
                "Set-Content -Path $wrapper -Value $lines -Encoding UTF8\n" +
                "Write-Host 'Wrapper created.'";
            await shell.RunCommandAsync(createWrapper, ct);

            // 4. Register a scheduled task to run the wrapper as the VM user.
            //    Boxstarter needs to set auto-login for the actual user account, so we run
            //    as flare with the password provided.  The -RunLevel Highest allows elevation.
            logger.LogInformation("Registering scheduled task '{TaskName}' for FLARE install as user {User}...", taskName, username);
            string registerTask =
                "$taskName = '" + taskName + "'\n" +
                "$wrapper = '" + wrapperQuoted + "'\n" +
                "$user = '" + usernameQuoted + "'\n" +
                "$pass = '" + passwordQuoted + "'\n" +
                "\n" +
                "# Remove any previous attempt\n" +
                "Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue\n" +
                "\n" +
                "# Build the action: run the wrapper script with the full PowerShell path\n" +
                "$psExe = 'C:\\\\Windows\\\\System32\\\\WindowsPowerShell\\\\v1.0\\\\powershell.exe'\n" +
                "$action = New-ScheduledTaskAction -Execute $psExe -Argument \"-ExecutionPolicy Bypass -File $wrapper\" -WorkingDirectory (Split-Path $wrapper)\n" +
                "\n" +
                "# No time limit — FLARE can take 30-60 minutes\n" +
                "$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries " +
                "-DontStopIfGoingOnBatteries -StartWhenAvailable " +
                "-ExecutionTimeLimit (New-TimeSpan -Days 1)\n" +
                "\n" +
                "# Register with user credentials so Boxstarter can set auto-login\n" +
                "Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User $user -Password $pass -RunLevel Highest -Force | Out-Null\n" +
                "Write-Host 'Task registered.'";
            await shell.RunCommandAsync(registerTask, ct);

            // 5. Start the task
            logger.LogInformation("Starting FLARE VM install task on VM {VMName}...", shell.VmName);
            await shell.RunCommandAsync("Start-ScheduledTask -TaskName '" + taskName + "'", ct);

            // 6. Poll briefly to confirm the task is actually running
            bool taskRunning = false;
            for (int i = 0; i < 6; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                try
                {
                    string state = await shell.RunCommandAsync("(Get-ScheduledTask -TaskName '" + taskName + "').State", ct);
                    if (state?.Trim() == "Running")
                    {
                        taskRunning = true;
                        break;
                    }
                }
                catch { /* task may not appear immediately */ }
            }

            if (taskRunning)
            {
                logger.LogInformation(
                    "FLARE VM installation is running in the background on VM {VMName}. " +
                    "It may take 30-60 minutes. Monitor progress via:\n" +
                    "  - Log file: {LogPath}\n" +
                    "  - Transcript: {ErrorLogPath}\n" +
                    "  - Desktop install.ps1 script\n" +
                    "Boxstarter will handle any required reboots automatically.",
                    shell.VmName, logPath, errorLogPath);
            }
            else
            {
                logger.LogWarning(
                    "FLARE VM install task state could not be confirmed on VM {VMName}. " +
                    "Check {LogPath} and {ErrorLogPath} for details.",
                    shell.VmName, logPath, errorLogPath);

                // Try to dump the last few lines of the log for immediate diagnosis
                try
                {
                    string tail = await shell.RunCommandAsync(
                        "if (Test-Path '" + logPathQuoted + "') { Get-Content '" + logPathQuoted + "' -Tail 20 } else { Write-Output 'Log file not found yet' }", ct);
                    logger.LogDebug("FLARE install log tail: {Tail}", tail);
                }
                catch { /* ignore log read failures */ }
            }

            // 7. The install script is kept on the Desktop (user requirement)
            logger.LogInformation("FLARE VM install script retained on Desktop for VM {VMName}", shell.VmName);
        }

        // ── IConfigurableCustomizationStep (UI metadata) ────────────────
        public string CardTitle => "Install FLARE VM";
        public string CardDescription => "Install the FLARE VM reverse engineering toolkit using Mandiant's install.ps1 script. Installs Chocolatey, Boxstarter, and 100+ malware analysis tools.";
        public string Label => "Install FLARE VM tools";
        public string Tooltip => "Downloads and runs the FLARE VM installer from mandiant/flare-vm. This installs the full FLARE VM toolkit for malware analysis including debuggers, disassemblers, decompilers, and network analysis tools. Installation may take 30-60 minutes.";
        public bool DefaultEnabled => true;
        public bool IsOptional => true;

        // ── IDistributionOptionMetadata (deploy-phase UI) ───────────────
        public string DeployTitle => "Install FLARE VM";
        public string DeployDescription => "Installing the FLARE VM malware analysis toolkit";
        public string DeployPhaseId => "Sub_InstallFlareVm";
        public string DeployIconName => "Toolbox24";
        public int DeployOrder => 200;
        public string? DeployCompletionInfo => "Please allow at least one hour for the FLARE VM scripts to finish configuring the machine";

        public bool IsVisibleFor(GalleryItem item)
            => string.Equals(item?.Name, "FLARE VM", StringComparison.OrdinalIgnoreCase);
    }
}
