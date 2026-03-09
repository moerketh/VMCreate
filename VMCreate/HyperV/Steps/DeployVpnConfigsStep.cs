using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Deploys .ovpn VPN configuration files to the guest and imports them
    /// into NetworkManager so they appear in the system tray VPN menu.
    /// Runs after <see cref="InstallOpenVpnStep"/> (Order 300 > 200).
    /// </summary>
    public class DeployVpnConfigsStep : ICustomizationStep
    {
        public string Name => "Deploy VPN Configs";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public int Order => 300;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations.ConfigureHtbVpn;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            // Deploy API-downloaded keys and import into NetworkManager
            if (customizations.HtbVpnKeys != null)
            {
                foreach (var key in customizations.HtbVpnKeys)
                {
                    string guestPath = $"/etc/openvpn/client/{key.GuestFileName}";
                    await shell.CopyContentAsync(key.OvpnContent, guestPath, ct);
                    logger.LogInformation("Deployed {Name} VPN config to {Path} on VM {VMName}",
                        key.Name, guestPath, shell.VmName);

                    // Import into NetworkManager so it appears in the system tray
                    string importResult = await shell.RunCommandAsync(
                        $"sudo nmcli connection import type openvpn file '{guestPath}' 2>&1", ct);

                    if (importResult != null && !importResult.Contains("Error"))
                    {
                        // Rename to prefix with "HTB" so VPN connections are easily identifiable
                        string connName = Path.GetFileNameWithoutExtension(key.GuestFileName);
                        string htbName = $"HTB {key.Name}";
                        await shell.RunCommandAsync(
                            $"sudo nmcli connection modify '{connName}' connection.id '{htbName}' 2>&1", ct);

                        logger.LogInformation("Imported {Name} VPN as '{HtbName}' into NetworkManager on VM {VMName}",
                            key.Name, htbName, shell.VmName);
                    }
                    else
                    {
                        logger.LogWarning("NetworkManager import failed for {Name}: {Output}",
                            key.Name, importResult?.Trim());
                    }
                }
            }

            // Deploy manual .ovpn file if provided
            if (!string.IsNullOrEmpty(customizations.OvpnFilePath) && File.Exists(customizations.OvpnFilePath))
            {
                string guestPath = "/etc/openvpn/client/manual.ovpn";
                await shell.CopyFileAsync(customizations.OvpnFilePath, guestPath, ct);

                string importResult = await shell.RunCommandAsync(
                    $"sudo nmcli connection import type openvpn file '{guestPath}' 2>&1", ct);

                if (importResult != null && !importResult.Contains("Error"))
                {
                    // Rename manual import to "HTB Manual" for consistency
                    await shell.RunCommandAsync(
                        "sudo nmcli connection modify 'manual' connection.id 'HTB Manual' 2>&1", ct);
                    logger.LogInformation("Imported manual .ovpn as 'HTB Manual' into NetworkManager on VM {VMName}", shell.VmName);
                }
                else
                    logger.LogWarning("NetworkManager import failed for manual .ovpn: {Output}", importResult?.Trim());
            }

            // Log final NM VPN connection state
            string connections = await shell.RunCommandAsync(
                "nmcli connection show 2>&1 | grep -i vpn || echo 'no-vpn-connections'", ct);
            logger.LogInformation("NetworkManager VPN connections: {Connections}", connections?.Trim());

            logger.LogInformation("HTB VPN configured for VM {VMName}", shell.VmName);
        }
    }
}
