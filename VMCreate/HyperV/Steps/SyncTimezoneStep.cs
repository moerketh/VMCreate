using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Syncs the host's timezone to the guest via timedatectl.
    /// Works on any Linux guest with systemd. Maps Windows timezone IDs to IANA format.
    /// </summary>
    public class SyncTimezoneStep : ICustomizationStep
    {
        public string Name => "Sync Timezone";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public int Order => 100;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations.SyncTimezone;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            var localTz = TimeZoneInfo.Local;
            string ianaId;

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(localTz.Id, out string converted))
            {
                ianaId = converted;
            }
            else
            {
                ianaId = localTz.Id;
                logger.LogWarning("Could not convert Windows timezone '{WinTz}' to IANA, using as-is", localTz.Id);
            }

            logger.LogInformation("Syncing timezone to {IanaId} (host: {WindowsId}) on VM {VMName}",
                ianaId, localTz.Id, shell.VmName);

            string result = await shell.RunCommandAsync(
                $"sudo timedatectl set-timezone '{ianaId}' && timedatectl status", ct);

            logger.LogDebug("timedatectl output: {Output}", result);
            logger.LogInformation("Timezone synced to host for VM {VMName}", shell.VmName);
        }
    }
}
