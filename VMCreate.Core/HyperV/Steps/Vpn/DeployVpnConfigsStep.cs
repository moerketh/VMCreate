using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Deploys .ovpn VPN configuration files to the guest and imports them
    /// into NetworkManager so they appear in the system tray VPN menu.
    /// Runs after <see cref="InstallOpenVpnStep"/> (Order 300 > 200).
    /// <para>
    /// When no keys were preloaded, downloads them here with the HTB App
    /// Token stored in Settings, so both front ends go by the stored token
    /// alone. A download failure is a warning, not a failed deployment.
    /// </para>
    /// </summary>
    public class DeployVpnConfigsStep : ICustomizationStep, IStepDisplayInfo
    {
        private readonly IHtbApiClient? _htbApiClient;
        private readonly IUserPreferencesStore? _preferencesStore;

        public DeployVpnConfigsStep(IHtbApiClient? htbApiClient = null, IUserPreferencesStore? preferencesStore = null)
        {
            _htbApiClient = htbApiClient;
            _preferencesStore = preferencesStore;
        }

        public string Name => "Deploy VPN Configs";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 300;
        public string? ProgressPhaseId => "Sub_ConfigureVpn";

        public string DeployTitle => "Configure VPN";
        public string DeployDescription => "Deploying VPN configs";
        public string DeployIconName => "Globe24";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => customizations?.ConfigureHtbVpn == true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            IReadOnlyList<HtbVpnKey> keys = customizations.HtbVpnKeys is { Count: > 0 }
                ? customizations.HtbVpnKeys
                : await DownloadKeysAsync(customizations.HtbVpnProducts, logger, ct);

            // Deploy API-downloaded keys and import into NetworkManager
            if (keys.Count > 0)
            {
                foreach (var key in keys)
                {
                    string guestPath = $"/etc/openvpn/client/{key.GuestFileName}";
                    // .ovpn bodies embed client certificates AND private keys —
                    // CopySecretAsync lands them as root:root 0600 (the old
                    // 644-everything copy contract left the keys world-readable
                    // for every local account on the VM).
                    await shell.CopySecretAsync(key.OvpnContent, guestPath, ct);
                    logger.LogInformation("Deployed {Name} VPN config to {Path} on VM {VMName} (root:root 0600)",
                        key.Name, guestPath, shell.VmName);

                    // Import into NetworkManager so it appears in the system tray
                    string safeGuestPath = EscapeSingleQuotes(guestPath);
                    string importResult = await shell.RunCommandAsync(
                        $"sudo nmcli connection import type openvpn file '{safeGuestPath}' 2>&1", ct);

                    if (importResult != null && !importResult.Contains("Error"))
                    {
                        // Rename to prefix with "HTB" so VPN connections are easily identifiable
                        string connName = Path.GetFileNameWithoutExtension(key.GuestFileName);
                        string htbName = $"HTB {key.Name}";
                        string safeConnName = EscapeSingleQuotes(connName);
                        string safeHtbName = EscapeSingleQuotes(htbName);
                        await shell.RunCommandAsync(
                            $"sudo nmcli connection modify '{safeConnName}' connection.id '{safeHtbName}' 2>&1", ct);

                        // Enable split tunneling: HTB VPNs only provide access to lab networks,
                        // never general internet. Without never-default, NetworkManager
                        // assigns the VPN a default route (metric 50) that hijacks all traffic
                        // through the tunnel, breaking connectivity.
                        await shell.RunCommandAsync(
                            $"sudo nmcli connection modify '{safeHtbName}' ipv4.never-default yes ipv6.never-default yes 2>&1", ct);

                        logger.LogInformation("Imported {Name} VPN as '{HtbName}' into NetworkManager (split tunneling enabled) on VM {VMName}",
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
                // .ovpn bodies embed client certificates and private keys —
                // copy as a secret (root:root 0600), like the API-downloaded
                // configs above.
                string manualContent = await File.ReadAllTextAsync(customizations.OvpnFilePath, ct);
                await shell.CopySecretAsync(manualContent, guestPath, ct);

                string safeGuestPath = EscapeSingleQuotes(guestPath);
                string importResult = await shell.RunCommandAsync(
                    $"sudo nmcli connection import type openvpn file '{safeGuestPath}' 2>&1", ct);

                if (importResult != null && !importResult.Contains("Error"))
                {
                    // Rename manual import to "HTB Manual" for consistency
                    await shell.RunCommandAsync(
                        "sudo nmcli connection modify 'manual' connection.id 'HTB Manual' 2>&1", ct);

                    // Enable split tunneling for the manual import as well (see HTB-key branch).
                    await shell.RunCommandAsync(
                        "sudo nmcli connection modify 'HTB Manual' ipv4.never-default yes ipv6.never-default yes 2>&1", ct);
                    logger.LogInformation("Imported manual .ovpn as 'HTB Manual' into NetworkManager (split tunneling enabled) on VM {VMName}", shell.VmName);
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

        /// <summary>
        /// Downloads the account's VPN keys with the stored App Token, keeping
        /// the products selected by <paramref name="products"/>. Returns an
        /// empty list, with a warning, when there is no token or the download
        /// fails.
        /// </summary>
        private async Task<IReadOnlyList<HtbVpnKey>> DownloadKeysAsync(string? products, ILogger logger, CancellationToken ct)
        {
            if (_htbApiClient == null || _preferencesStore == null)
                return Array.Empty<HtbVpnKey>();

            string? token = _preferencesStore.Load().HtbApiToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                logger.LogInformation("No HTB App Token in Settings; no HTB VPN keys to download");
                return Array.Empty<HtbVpnKey>();
            }

            try
            {
                var results = await _htbApiClient.DownloadAllKeysAsync(token, ct);
                foreach (var failed in results.Where(r => !r.Success))
                    logger.LogWarning("HTB VPN key for {Name} not downloaded: {Error}", failed.EndpointName, failed.ErrorMessage);

                return results
                    .Where(r => r.Success && r.Key != null && HtbApiClient.MatchesProductFilter(r.Product, products))
                    .Select(r => r.Key!)
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "HTB VPN key download failed; continuing without HTB VPN configs");
                return Array.Empty<HtbVpnKey>();
            }
        }

        /// <summary>
        /// Escapes a value for safe embedding inside a single-quoted bash string.
        /// </summary>
        private static string EscapeSingleQuotes(string value) =>
            value.Replace("'", "'\\''");
    }
}
