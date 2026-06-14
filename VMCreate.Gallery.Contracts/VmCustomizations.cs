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
        public bool ConfigureXrdp { get; set; }

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

        /// <summary>When true, install OpenVPN and deploy VPN configs to the VM.</summary>
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
        /// </summary>
        public bool HasPreBootCustomizations => ConfigureXrdp;

        /// <summary>
        /// Returns true if any post-boot customizations are enabled
        /// (i.e. options that require SSH into the running VM).
        /// </summary>
        public bool HasPostBootCustomizations =>
            ConfigureHtbVpn || SyncTimezone || DistributionOptions.Any(o => o.IsEnabled);
    }
}
