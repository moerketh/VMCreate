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
        public string Name { get; set; } = null!;

        /// <summary>Whether the user enabled this option.</summary>
        public bool IsEnabled { get; set; }
    }

    /// <summary>
    /// Elastic SIEM AI configuration for a single deployment. Hydrated by the
    /// front ends from the machine-level credential store
    /// (<see cref="UserPreferences"/>) right before deployment — secrets live
    /// in memory only, are never logged (see <see cref="SecretMask"/>) and are
    /// never sent over KVP; the Elastic step delivers them to Elasticsearch's
    /// inference API itself.
    /// </summary>
    public class ElasticAiSettings
    {
        /// <summary>Enable AI features on the deployed Elastic Stack (inference endpoint).</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Start a 30-day Enterprise <c>start_trial</c> license. AI features
        /// (AI Assistant, Attack Discovery) require Enterprise on self-hosted
        /// stacks; without this the SIEM runs on the free Basic license.
        /// </summary>
        public bool StartTrialLicense { get; set; }

        /// <summary>OpenAI-compatible chat-completions URL (full endpoint path).</summary>
        public string? ChatCompletionsUrl { get; set; }

        /// <summary>API key for the endpoint (dummy values are valid for local LLMs).</summary>
        public string? ApiKey { get; set; }

        /// <summary>Model id passed to the endpoint.</summary>
        public string? Model { get; set; }

        /// <summary>True when <see cref="Enabled"/> and all fields are present.</summary>
        public bool IsComplete =>
            Enabled
            && !string.IsNullOrWhiteSpace(ChatCompletionsUrl)
            && !string.IsNullOrWhiteSpace(ApiKey)
            && !string.IsNullOrWhiteSpace(Model);
    }

    public class VmCustomizations
    {
        /// <summary>
        /// The RDP server backend to install on Linux guests. Default is
        /// <see cref="RdpBackend.Auto"/>: no backend is guessed statically —
        /// a detection step resolves <see cref="RdpBackend.Auto"/> to a concrete
        /// backend (<see cref="RdpBackend.Lamco"/>, <see cref="RdpBackend.Xrdp"/>,
        /// or <see cref="RdpBackend.None"/> for Nix-family guests, which have
        /// no installable RDP backend) at deployment time, after the guest is
        /// reachable over SSH (see <c>AutoRdpBackendResolveStep</c>). When set to
        /// <see cref="RdpBackend.Lamco"/>, the Wayland-disable / X11-force steps
        /// are skipped (Lamco is Wayland-native) and graphical autologin is
        /// enabled instead so Lamco has a live session to share. When set to
        /// <see cref="RdpBackend.Xrdp"/>, xrdp is pre-installed via the cloning-ISO
        /// chroot and Wayland is disabled for maximum compatibility.
        /// </summary>
        public RdpBackend RdpBackend { get; set; } = RdpBackend.Auto;

        /// <summary>
        /// Backward-compatible view over <see cref="RdpBackend"/>: true only when
        /// the backend is concretely <see cref="RdpBackend.Xrdp"/>. The setter maps
        /// true → <see cref="RdpBackend.Xrdp"/> and false → <see cref="RdpBackend.None"/>,
        /// preserving the behavior of existing call sites (CLI <c>--no-xrdp</c>,
        /// persisted settings, KVP sender, ISO-boot trigger, deploy UI) without
        /// requiring them to know about <see cref="RdpBackend"/>. Note that
        /// <see cref="RdpBackend.Auto"/> reads as <b>false</b> here: while Auto is
        /// unresolved, no pre-boot xrdp work may start (xrdp and Lamco conflict
        /// on port 3389, and the resolved backend is not yet known). New code
        /// should read/write <see cref="RdpBackend"/> directly.
        /// </summary>
        public bool ConfigureXrdp
        {
            get => RdpBackend == RdpBackend.Xrdp;
            set => RdpBackend = value ? RdpBackend.Xrdp : RdpBackend.None;
        }

        /// <summary>
        /// Console resolution forced on Linux guests whose RDP backend shares
        /// the physical console (Lamco), formatted
        /// <c>&lt;width&gt;x&lt;height&gt;@&lt;rate&gt;</c> (e.g. <c>1920x1080@60</c>).
        /// Substituted host-side into the guest scripts — see
        /// <c>ConsoleResolutionValidator</c> for the shape contract and why
        /// the value must be validated before substitution.
        /// </summary>
        public string ConsoleResolution { get; set; } = "1920x1080@60";

        /// <summary>
        /// DNS configuration mode. Default is <see cref="DnsMode.Host"/>
        /// which resolves the host machine's DNS servers and sends them to the guest.
        /// </summary>
        public DnsMode DnsMode { get; set; } = DnsMode.Host;

        /// <summary>
        /// Comma-separated nameserver IP addresses for <see cref="DnsMode.Custom"/> mode.
        /// Example: "9.9.9.9,1.1.1.1"
        /// </summary>
        public string? CustomNameservers { get; set; }

        /// <summary>
        /// Static guest IPv4 in CIDR form (<c>192.168.10.50/24</c>). When set,
        /// the guest's first boot applies it from the NoCloud seed's
        /// <c>network-config</c> (v2) instead of DHCP, and every host-side SSH
        /// connection targets this address directly — Hyper-V's IP discovery
        /// leans on DHCP leases and neighbor-table ARP learning and may never
        /// see a statically configured guest. IPv4 only.
        /// </summary>
        public string? StaticIp { get; set; }

        /// <summary>
        /// Default gateway for <see cref="StaticIp"/> (IPv4). Optional: when
        /// omitted the seed carries no default route and the guest can only
        /// reach its own subnet.
        /// </summary>
        public string? StaticGateway { get; set; }

        /// <summary>
        /// One or comma-separated nameserver IPv4 addresses for
        /// <see cref="StaticIp"/>. Optional. Distinct from
        /// <see cref="DnsMode"/>/<see cref="CustomNameservers"/>, which drive
        /// post-boot guest DNS configuration; these ride the NoCloud seed
        /// alongside the static address.
        /// </summary>
        public string? StaticDns { get; set; }

        /// <summary>True when <see cref="StaticIp"/> requests static addressing.</summary>
        public bool HasStaticNetwork => !string.IsNullOrWhiteSpace(StaticIp);

        /// <summary>
        /// The host part of <see cref="StaticIp"/> — the address SSH connects
        /// to. The seed carries the full CIDR (the prefix drives the guest's
        /// netmask); the connection target carries the address only.
        /// </summary>
        public string? StaticIpHost => StaticIp is null ? null : StaticIp.Split('/')[0];

        /// <summary>
        /// When true (default), install OpenVPN and the NetworkManager OpenVPN plugin
        /// on Linux guests. Independent of <see cref="ConfigureHtbVpn"/> so the user can
        /// still install OpenVPN manually later even when no HTB config is supplied.
        /// </summary>
        public bool InstallOpenVpn { get; set; } = true;

        /// <summary>When true, deploy VPN configs to the VM (requires an .ovpn source).</summary>
        public bool ConfigureHtbVpn { get; set; }

        /// <summary>Host path to a manually selected .ovpn file (fallback).</summary>
        public string? OvpnFilePath { get; set; }

        /// <summary>Pre-downloaded HTB VPN keys to deploy to the VM.</summary>
        public List<HtbVpnKey> HtbVpnKeys { get; set; } = new();

        /// <summary>When true, read the host timezone and set it on the guest.</summary>
        public bool SyncTimezone { get; set; }

        /// <summary>
        /// Optional path to a custom SSH public key file.
        /// When null/empty, the auto-generated per-user key is used.
        /// </summary>
        public string? CustomSshPublicKeyPath { get; set; }

        /// <summary>
        /// When true (default), enable Hyper-V Guest Service Interface and
        /// Enhanced Session Mode (clipboard, drive redirection, IP discovery).
        /// </summary>
        public bool EnableIntegrationServices { get; set; } = true;

        /// <summary>
        /// The desktop environment to switch the Linux guest to, as an id from
        /// the distribution's <see cref="IDesktopEnvironmentCatalog"/> ("kde",
        /// "gnome", "xfce", …). Null (default) keeps the image's stock desktop.
        /// Not a <see cref="DistributionOptionSelection"/>: the desktop choice
        /// is a single exclusive pick, not an enable/disable toggle, and it
        /// couples with <see cref="RdpBackend"/> (an X11-only desktop rules out
        /// the Wayland-native Lamco backend). Resolved and applied post-boot by
        /// <c>DesktopSwitchStep</c>, before the RDP backend auto-resolution.
        /// </summary>
        public string? DesktopEnvironmentId { get; set; }

        /// <summary>
        /// True while an Auto RDP backend cannot be resolved yet because a
        /// desktop switch is still pending: any display-server verdict before
        /// the switch reads the STOCK desktop. Everything that depends on the
        /// resolved backend — the resolver itself and the xrdp/X11 fixups —
        /// waits; <c>DesktopSwitchStep</c> clears <see cref="DesktopEnvironmentId"/>
        /// when the switch lands, and the post-boot loop re-evaluates them.
        /// (Live: Kali + GNOME ran the X11 fixups in the ISO stage under the
        /// unresolved Auto, pre-seeding gdm3's WaylandEnable=false, so the
        /// switched GNOME came up X11-only and Auto picked xrdp.)
        /// </summary>
        public bool AutoRdpAwaitsDesktopSwitch =>
            RdpBackend == RdpBackend.Auto && !string.IsNullOrEmpty(DesktopEnvironmentId);

        /// <summary>
        /// Per-distribution options selected by the user on the customization page.
        /// Order is preserved so the deployment UI can show cards in the intended sequence.
        /// </summary>
        public List<DistributionOptionSelection> DistributionOptions { get; set; } = new();

        /// <summary>
        /// AI enablement for Elastic SIEM deployments (and the Elastic add-on step).
        /// Filled from the machine-level LLM credentials by the front ends; see
        /// <see cref="ElasticAiSettings"/> for the secrecy rules.
        /// </summary>
        public ElasticAiSettings ElasticAi { get; set; } = new();

        /// <summary>
        /// Optional path to a cloud-init user-data file supplied by the user
        /// (CLI <c>--cloud-init</c>). When set, the deployment's NoCloud seed
        /// merges the file's content ahead of the built-in cloud-config block
        /// via MIME multipart — cloud-init runs both parts, and on conflicting
        /// top-level cloud-config keys the built-in block (automation user,
        /// SSH key, passwordless sudo, growpart) wins, so post-boot SSH
        /// machinery keeps working. Setting this on an image without the
        /// <c>cloud-init</c> tag still forces the seed path (see
        /// <c>DiskImageVmCreationStrategy</c>), which is what makes a raw
        /// cloud image deployable unattended.
        /// </summary>
        public string? CloudInitUserDataPath { get; set; }

        /// <summary>
        /// Returns true if any pre-boot customizations are enabled
        /// (i.e. options applied during ISO customization before first boot).
        /// Only xrdp uses the pre-boot (cloning-ISO) path. Lamco installs
        /// post-boot over SSH, and Auto installs nothing pre-boot (the backend
        /// is resolved post-boot; the Auto→Xrdp outcome backfills the xrdp
        /// install via a post-boot step), so neither triggers the ISO boot
        /// cycle through this property.
        /// </summary>
        public bool HasPreBootCustomizations => RdpBackend == RdpBackend.Xrdp;

        /// <summary>
        /// Returns true if any post-boot customizations are enabled
        /// (i.e. options that require SSH into the running VM).
        /// </summary>
        public bool HasPostBootCustomizations =>
            InstallOpenVpn || ConfigureHtbVpn || SyncTimezone
            || !string.IsNullOrEmpty(DesktopEnvironmentId)
            || DistributionOptions.Any(o => o.IsEnabled);
    }
}
