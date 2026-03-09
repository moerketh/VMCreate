using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Installs OpenVPN and the NetworkManager OpenVPN plugin on the guest.
    /// Auto-detects the package manager (apt, dnf, yum, pacman, zypper).
    /// After install, restarts NetworkManager so the VPN plugin is loaded.
    /// </summary>
    public class InstallOpenVpnStep : ICustomizationStep
    {
        public string Name => "Install OpenVPN";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public int Order => 200;

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations.ConfigureHtbVpn;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Installing OpenVPN and NetworkManager plugin on VM {VMName}", shell.VmName);

            string installCmd = @"
                if command -v apt-get >/dev/null 2>&1; then
                    sudo DEBIAN_FRONTEND=noninteractive apt-get update -y -qq && sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq openvpn network-manager-openvpn network-manager-openvpn-gnome 2>&1
                elif command -v dnf >/dev/null 2>&1; then
                    sudo dnf install -y -q openvpn NetworkManager-openvpn NetworkManager-openvpn-gnome 2>&1
                elif command -v yum >/dev/null 2>&1; then
                    sudo yum install -y -q openvpn NetworkManager-openvpn NetworkManager-openvpn-gnome 2>&1
                elif command -v pacman >/dev/null 2>&1; then
                    sudo pacman -Sy --noconfirm openvpn networkmanager-openvpn 2>&1
                elif command -v zypper >/dev/null 2>&1; then
                    sudo zypper install -y openvpn NetworkManager-openvpn NetworkManager-openvpn-gnome 2>&1
                else
                    echo 'ERROR: Unknown package manager' >&2
                    exit 1
                fi
            ";

            string result = await shell.RunCommandAsync(installCmd, ct);
            logger.LogDebug("OpenVPN install output: {Output}", result);

            // Restart NetworkManager to pick up the new OpenVPN plugin
            await shell.RunCommandAsync("sudo systemctl restart NetworkManager 2>&1 || true", ct);

            // Verify
            string verify = await shell.RunCommandAsync(
                "command -v openvpn || /usr/sbin/openvpn --version 2>&1 | head -1", ct);
            logger.LogInformation("OpenVPN installed: {Version}", verify?.Trim());
        }
    }
}
