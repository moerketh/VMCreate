using System.Collections.Generic;
using System.Linq;

namespace VMCreate
{
    public class VmCustomizations
    {
        public bool ConfigureXrdp { get; set; }

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
        /// Set to false for maximum VM isolation (e.g. malware analysis).
        /// </summary>
        public bool EnableIntegrationServices { get; set; } = true;

        /// <summary>
        /// Per-distribution options populated by <see cref="IConfigurableCustomizationStep"/> UI cards.
        /// Keys are step names; values indicate whether the user enabled the option.
        /// </summary>
        public Dictionary<string, bool> DistributionOptions { get; set; } = new();

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
            ConfigureHtbVpn || SyncTimezone || DistributionOptions.Any(kv => kv.Value);
    }
}
