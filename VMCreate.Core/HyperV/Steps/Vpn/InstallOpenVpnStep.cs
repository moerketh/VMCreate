using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Installs OpenVPN and the NetworkManager OpenVPN plugin on the guest.
    /// Auto-detects the package manager (apt, dnf, yum, pacman, zypper).
    /// Best-effort: on distros where the packages are not in the enabled
    /// repositories (e.g. RHEL-family EL10 needs EPEL), the deployment
    /// continues and a warning is logged rather than the whole run failing
    /// - mirroring DeployVpnConfigsStep, a convenience install must never
    /// kill a deployment. After install, restarts NetworkManager so the
    /// VPN plugin is loaded.
    /// </summary>
    public class InstallOpenVpnStep : ICustomizationStep
    {
        public string Name => "Install OpenVPN";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 200;
        public string ProgressPhaseId => "Sub_InstallOpenVpn";

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => customizations?.InstallOpenVpn == true;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Installing OpenVPN and NetworkManager plugin on VM {VMName}", shell.VmName);

            // Every branch neutralises its exit status: the SSH automation
            // path treats non-zero guest exits as fatal for the deployment,
            // and a missing optional package must not abort a VM build.
            string installCmd = @"
                if command -v apt-get >/dev/null 2>&1; then
                    sudo DEBIAN_FRONTEND=noninteractive apt-get update -y -qq 2>&1 || echo 'WARNING: apt update failed'
                    sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq openvpn network-manager-openvpn network-manager-openvpn-gnome 2>&1 || echo 'WARNING: openvpn packages unavailable via apt'
                elif command -v dnf >/dev/null 2>&1; then
                    sudo dnf install -y -q epel-release 2>/dev/null && sudo dnf config-manager --set-enabled epel 2>/dev/null || true
                    sudo dnf install -y -q openvpn NetworkManager-openvpn NetworkManager-openvpn-gnome 2>&1 \
                        || sudo dnf install -y -q openvpn NetworkManager-openvpn 2>&1 \
                        || echo 'WARNING: openvpn packages unavailable in enabled dnf repos (EPEL may be required on this distro)'
                elif command -v yum >/dev/null 2>&1; then
                    sudo yum install -y -q epel-release 2>/dev/null || true
                    sudo yum install -y -q openvpn NetworkManager-openvpn NetworkManager-openvpn-gnome 2>&1 \
                        || sudo yum install -y -q openvpn NetworkManager-openvpn 2>&1 \
                        || echo 'WARNING: openvpn packages unavailable in enabled yum repos (EPEL may be required on this distro)'
                elif command -v pacman >/dev/null 2>&1; then
                    sudo pacman -Sy --noconfirm openvpn networkmanager-openvpn 2>&1 || echo 'WARNING: openvpn packages unavailable via pacman'
                elif command -v zypper >/dev/null 2>&1; then
                    sudo zypper --non-interactive install openvpn NetworkManager-openvpn 2>&1 || echo 'WARNING: openvpn packages unavailable via zypper'
                else
                    echo 'WARNING: unknown package manager - skipping OpenVPN install'
                fi
            ";

            string result = await shell.RunCommandAsync(installCmd, ct);
            logger.LogDebug("OpenVPN install output: {Output}", result);

            // Restart NetworkManager to pick up the new OpenVPN plugin
            await shell.RunCommandAsync("sudo systemctl restart NetworkManager 2>&1 || true", ct);

            // Verify (also neutralised: the transport throws on non-zero exits)
            string verify = await shell.RunCommandAsync(@"
                if command -v openvpn >/dev/null 2>&1; then
                    openvpn --version 2>&1 | head -1
                else
                    echo 'WARNING: openvpn binary not present after install'
                fi
            ", ct);

            if (verify == null || verify.Contains("WARNING"))
            {
                logger.LogWarning(
                    "OpenVPN packages are unavailable on this distro (VM {VMName}) - VPN imports in later steps will fail if openvpn stays missing. Output: {Output}",
                    shell.VmName, (result + System.Environment.NewLine + verify)?.Trim());
            }
            else
            {
                logger.LogInformation("OpenVPN installed: {Version}", verify.Trim());
            }
        }
    }
}