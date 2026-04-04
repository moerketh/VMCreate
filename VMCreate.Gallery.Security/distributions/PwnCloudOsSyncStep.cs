using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery.distributions
{
    /// <summary>
    /// Ensures pwncloudos-sync is installed and runs it to update all
    /// PwnCloudOS security tools to their latest versions.
    /// </summary>
    public class PwnCloudOsSyncStep : IConfigurableCustomizationStep
    {
        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "PwnCloudOS Sync";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public int Order => 250;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => IsVisibleFor(item)
               && customizations.DistributionOptions.TryGetValue(Name, out bool enabled)
               && enabled;

        public async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            const string syncDir = "/opt/pwncloudos-sync";
            const string repoUrl = "https://github.com/pwnedlabs/pwncloudos-sync.git";

            try
            {
                // 1. Check if pwncloudos-sync is already installed
                string check = await shell.RunCommandAsync(
                    $"test -d {syncDir} && echo EXISTS || echo ABSENT", ct);

                if (check.Trim().Equals("ABSENT", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("pwncloudos-sync not found — cloning from GitHub on VM {VMName}", shell.VmName);
                    await shell.RunCommandAsync(
                        $"sudo git clone {repoUrl} {syncDir} 2>&1", ct);
                }
                else
                {
                    logger.LogInformation("pwncloudos-sync already installed at {Path} on VM {VMName}", syncDir, shell.VmName);
                    // Pull latest changes so manifests are up to date
                    await shell.RunCommandAsync(
                        $"cd {syncDir} && sudo git pull 2>&1 || true", ct);
                }

                // 2. Install / update Python dependencies
                logger.LogInformation("Installing pwncloudos-sync dependencies on VM {VMName}", shell.VmName);
                await shell.RunCommandAsync(
                    $"sudo pip3 install -r {syncDir}/requirements.txt --break-system-packages 2>&1", ct);

                // 3. Drop a wrapper script on PATH so the user can just type 'pwncloudos-sync'
                await shell.RunCommandAsync(
                    $"printf '#!/bin/sh\\ncd {syncDir} && exec python3 -m src.main \"$@\"\\n' | sudo tee /usr/local/bin/pwncloudos-sync > /dev/null && sudo chmod +x /usr/local/bin/pwncloudos-sync", ct);

                // 4. Run the updater (--all -y = update everything, skip confirmation)
                logger.LogInformation("Running pwncloudos-sync --all -y on VM {VMName}", shell.VmName);
                string output = await shell.RunCommandAsync(
                    $"cd {syncDir} && sudo python3 -m src.main --all -y 2>&1", ct);

                logger.LogInformation("pwncloudos-sync output for VM {VMName}:\n{Output}", shell.VmName, output);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Tool sync is best-effort — log and continue so it doesn't kill the deployment
                logger.LogWarning(ex, "pwncloudos-sync failed on VM {VMName}; the VM is still usable — you can run the tool manually later", shell.VmName);
            }
        }

        // ── IConfigurableCustomizationStep (UI metadata) ────────────────
        public string CardTitle => "PwnCloudOS Tools";
        public string CardDescription => "Install or update all PwnCloudOS security tools using pwncloudos-sync.";
        public string Label => "Update all tools (pwncloudos-sync)";
        public string Tooltip => "Checks if pwncloudos-sync is installed at /opt/pwncloudos-sync and clones it from GitHub if missing, then runs the tool updater to bring all 44 security tools to their latest version.";
        public bool DefaultEnabled => true;

        public bool IsVisibleFor(GalleryItem item)
            => string.Equals(item?.Name, "PwnCloudOS", StringComparison.OrdinalIgnoreCase);
    }
}
