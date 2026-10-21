using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Syncs the host's timezone to the guest. Maps Windows timezone IDs to
    /// IANA format and applies them with the mechanics the shell context
    /// allows: <c>timedatectl</c> on the live system (systemd re-reads the
    /// change immediately), or direct file updates in the ISO-stage chroot
    /// (<c>ln -sf</c> of the zoneinfo file onto /etc/localtime, plus
    /// /etc/timezone for Debian-family tools) — timedatectl needs the
    /// systemd manager, which a chroot does not have. First boot picks the
    /// file state up natively.
    /// </summary>
    public class SyncTimezoneStep : ICustomizationStep, IStepDisplayInfo
    {
        public string Name => "Sync Timezone";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 100;
        public string? ProgressPhaseId => "Sub_SyncTimezone";
        public bool SupportsChroot => true;

        public string DeployTitle => "Sync Timezone";
        public string DeployDescription => "Setting guest timezone to match host";
        public string DeployIconName => "Clock24";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => customizations?.SyncTimezone == true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            var localTz = TimeZoneInfo.Local;
            string ianaId;

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(localTz.Id, out string? converted) && converted != null)
            {
                ianaId = converted;
            }
            else
            {
                ianaId = localTz.Id;
                logger.LogWarning("Could not convert Windows timezone '{WinTz}' to IANA, using as-is", localTz.Id);
            }

            logger.LogInformation("Syncing timezone to {IanaId} (host: {WindowsId}) on VM {VMName} ({Context})",
                ianaId, localTz.Id, shell.VmName, shell.Context);

            string command;
            if (shell.Context == GuestExecutionContext.Chroot)
            {
                // No systemd manager in the chroot: point /etc/localtime at
                // the zoneinfo file directly and keep /etc/timezone (read by
                // Debian-family tools) in sync. Single-quoted for safety even
                // though IANA ids never contain shell metacharacters.
                command = $"sudo ln -sf '/usr/share/zoneinfo/{ianaId}' /etc/localtime" +
                          $" && printf '%s\\n' '{ianaId}' | sudo tee /etc/timezone >/dev/null" +
                          " && echo TIMEZONE_FILE_OK";
            }
            else
            {
                command = $"sudo timedatectl set-timezone '{ianaId}' && timedatectl status";
            }

            string result = await shell.RunCommandAsync(command, ct);

            if (shell.Context == GuestExecutionContext.Chroot)
            {
                // RunCommandAsync throws on non-zero exits; a zero exit
                // without the marker means the command ran but the update
                // did not verifiably happen — treat as failure.
                if (!result.Contains("TIMEZONE_FILE_OK", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Timezone file update on VM {shell.VmName} did not confirm success " +
                        "(no TIMEZONE_FILE_OK marker); the target's /etc/localtime state is unknown.");
            }
            else
            {
                logger.LogDebug("timedatectl output: {Output}", result);
            }

            logger.LogInformation("Timezone synced to host for VM {VMName}", shell.VmName);
        }
    }
}
