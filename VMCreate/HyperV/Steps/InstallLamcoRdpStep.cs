using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Installs and configures the Lamco RDP Server (Wayland-native RDP) on the
    /// guest VM via native deb/rpm packages from GitHub Releases.
    /// <para>
    /// Unlike xrdp (which installs via the external cloning-ISO chroot), Lamco
    /// installs post-boot over SSH. It is a Wayland-native server built on IronRDP
    /// with PipeWire-based screen capture, so Wayland is kept
    /// enabled (the <see cref="ForceX11Step"/>/<see cref="DisableWaylandSessionsStep"/>
    /// steps are skipped via <see cref="VmCustomizations.RdpBackend"/> gating).
    /// </para>
    /// <para>
    /// This step downloads the matching release asset for the detected distro
    /// (deb for Debian/Ubuntu/Parrot, rpm for Fedora, rpm for openSUSE) from
    /// <c>github.com/lamco-admin/lamco-rdp-server/releases</c>, installs it via
    /// the native package manager, installs Portal/PipeWire runtime deps, generates
    /// TLS certificates, writes <c>/etc/lamco-rdp-server/config.toml</c> (hybrid
    /// security, no auth), installs the systemd <b>user</b> service unit, and enables linger
    /// so the user service starts at boot. The one-time Portal permission grant
    /// (<c>--grant-permission</c>) is an interactive GUI dialog and is left as a
    /// manual post-deploy step — <see cref="EnableGraphicalAutologinStep"/>
    /// ensures a Wayland session exists for the user to grant into.
    /// </para>
    /// <para>
    /// PoC gating: only runs when <see cref="VmCustomizations.RdpBackend"/> is
    /// <see cref="RdpBackend.Lamco"/> and the gallery item's
    /// <see cref="GalleryItem.LinuxDistro"/> is one of the supported distributions
    /// (Ubuntu, Fedora, Debian, openSUSE, Parrot). <see cref="DistroDetector"/>
    /// re-verifies at runtime as a defensive check.
    /// </para>
    /// <para>
    /// Runs at Order 235, before <see cref="EnableGraphicalAutologinStep"/> (238)
    /// and before the xrdp block (240-270, skipped for Lamco).
    /// </para>
    /// </summary>
    public class InstallLamcoRdpStep : ICustomizationStep
    {
        public string Name => "Install Lamco RDP Server";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 235;
        public string? ProgressPhaseId => "Sub_InstallLamcoRdp";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations.RdpBackend == RdpBackend.Lamco && item.SupportsLamco();

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Installing Lamco RDP Server on VM {VMName}", shell.VmName);

            string script = ScriptResourceLoader.Load("install_lamco.sh");
            await shell.CopyContentAsync(script, "/tmp/install_lamco.sh", ct);

            // The install pulls apt packages and downloads the fork deb —
            // beyond the transport's default command timeout, but well under
            // 20 minutes now that the on-VM Rust build fallback is gone.
            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/install_lamco.sh && sudo rm -f /tmp/install_lamco.sh",
                TimeSpan.FromMinutes(20), ct);

            logger.LogInformation("Lamco RDP Server install result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }
    }
}
