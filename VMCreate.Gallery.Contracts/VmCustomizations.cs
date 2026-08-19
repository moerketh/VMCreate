using System.Collections.Generic;
using System.Linq;

namespace VMCreate
{
    /// <summary>
    /// Controls how DNS nameservers are configured on the guest VM.
    /// </summary>
    public enum DnsMode
    {
        /// <summary>Use the Windows host machine's DNS servers (default).</summary>
        Host,
        /// <summary>Use user-specified nameservers.</summary>
        Custom,
    }

    /// <summary>
    /// A single user-selected distribution-specific option stored in <see cref="VmCustomizations"/>.
    /// </summary>
    public class DistributionOptionSelection
    {
        /// <summary>Step name — matches <see cref="ICustomizationStep.Name"/>.</summary>
        public string Name { get; set; }

        /// <summary>Whether the user enabled this option.</summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Display/execution order within the post-boot phase. Provided by the step's
        /// <see cref="IDistributionOptionMetadata.DeployOrder"/> so the GUI doesn't need
        /// a hard-coded ordering list.
        /// </summary>
        public int Order { get; set; }
    }

    public class VmCustomizations
    {
        /// <summary>
        /// The RDP server backend to install on Linux guests. Default is
        /// <see cref="RdpBackend.Xrdp"/> for maximum compatibility. When set to
        /// <see cref="RdpBackend.Lamco"/>, the Wayland-disable / X11-force steps
        /// are skipped (Lamco is Wayland-native) and graphical autologin is
        /// enabled instead so Lamco has a live session to share.
        /// </summary>
        public RdpBackend RdpBackend { get; set; } = RdpBackend.Xrdp;

        /// <summary>
        /// Backward-compatible view over <see cref="RdpBackend"/>: true when the
        /// xrdp backend is selected, false otherwise. The setter maps
        /// true → <see cref="RdpBackend.Xrdp"/> and false → <see cref="RdpBackend.None"/>,
        /// preserving the behavior of existing call sites (CLI <c>--no-xrdp</c>,
        /// persisted settings, KVP sender, ISO-boot trigger, deploy UI) without
        /// requiring them to know about <see cref="RdpBackend"/>. New code should
        /// read/write <see cref="RdpBackend"/> directly.
        /// </summary>
        public bool ConfigureXrdp
        {
            get => RdpBackend == RdpBackend.Xrdp;
            set => RdpBackend = value ? RdpBackend.Xrdp : RdpBackend.None;
        }

        /// <summary>
        /// DNS configuration mode. Default is <see cref="DnsMode.Host"/>
        /// which resolves the host machine's DNS servers and sends them to the guest.
        /// </summary>
        public DnsMode DnsMode { get; set; } = DnsMode.Host;

        /// <summary>
        /// Comma-separated nameserver IP addresses for <see cref="DnsMode.Custom"/> mode.
        /// Example: "9.9.9.9,1.1.1.1"
        /// </summary>
        public string CustomNameservers { get; set; }

        /// <summary>
        /// When true (default), install OpenVPN and the NetworkManager OpenVPN plugin
        /// on Linux guests. Independent of <see cref="ConfigureHtbVpn"/> so the user can
        /// still install OpenVPN manually later even when no HTB config is supplied.
        /// </summary>
        public bool InstallOpenVpn { get; set; } = true;

        /// <summary>When true, deploy VPN configs to the VM (requires an .ovpn source).</summary>
        public bool ConfigureHtbVpn { get; set; }

        /// <summary>Host path to a manually selected .ovpn file (fallback).</summary>
        public string OvpnFilePath { get; set; }

        /// <summary>Pre-downloaded HTB VPN keys to deploy to the VM.</summary>
        public List<HtbVpnKey> HtbVpnKeys { get; set; } = new();

        /// <summary>When true, read the host timezone and set it on the guest.</summary>
        public bool SyncTimezone { get; set; }

        /// <summary>
        /// Optional path to a custom SSH public key file.
        /// When null/empty, the auto-generated per-user key is used.
        /// </summary>
        public string CustomSshPublicKeyPath { get; set; }

        /// <summary>
        /// When true (default), enable Hyper-V Guest Service Interface and
        /// Enhanced Session Mode (clipboard, drive redirection, IP discovery).
        /// </summary>
        public bool EnableIntegrationServices { get; set; } = true;

        /// <summary>
        /// Per-distribution options selected by the user on the customization page.
        /// Order is preserved so the deployment UI can show cards in the intended sequence.
        /// </summary>
        public List<DistributionOptionSelection> DistributionOptions { get; set; } = new();

        /// <summary>
        /// Returns true if any pre-boot customizations are enabled
        /// (i.e. options applied during ISO customization before first boot).
        /// Only xrdp uses the pre-boot (cloning-ISO) path. Lamco installs
        /// post-boot over SSH, so it does not trigger the ISO boot cycle.
        /// </summary>
        public bool HasPreBootCustomizations => RdpBackend == RdpBackend.Xrdp;

        /// <summary>
        /// Returns true if any post-boot customizations are enabled
        /// (i.e. options that require SSH into the running VM).
        /// </summary>
        public bool HasPostBootCustomizations =>
            InstallOpenVpn || ConfigureHtbVpn || SyncTimezone || DistributionOptions.Any(o => o.IsEnabled);
    }
}
