using System.Text.Json.Serialization;

namespace VMCreate
{
    /// <summary>
    /// Machine-level user preferences persisted across wizard runs
    /// (<c>%LocalAppData%\VMCreate\settings.json</c>). Two kinds of content live
    /// here:
    /// <list type="bullet">
    /// <item><b>Credentials / identity</b> — HTB API token, LLM API key: things the
    /// user types once. Stored DPAPI-encrypted (see <see cref="UserPreferencesStore"/>);
    /// the wizard never re-asks for them, only offers "change…".</item>
    /// <item><b>Habitual defaults</b> — DNS mode, RDP backend, VM sizing: the wizard
    /// pre-fills from these, every run can deviate, and the last-used values are
    /// saved back on deploy.</item>
    /// </list>
    /// Genuinely per-VM data (VM name, image selection, distribution options,
    /// VPN on/off) stays in <see cref="WizardData"/> / <see cref="VmCustomizations"/>
    /// and is never persisted here.
    /// <para>
    /// Defaults mirror the historical <see cref="VmSettings"/> / <see cref="AppSettings"/>
    /// hardcoded values so that a fresh install with no settings file behaves
    /// exactly like the pre-preferences wizard.
    /// </para>
    /// </summary>
    public class UserPreferences
    {
        // ── Window / appearance ────────────────────────────────────────

        /// <summary>Theme: "System" (default, follows Windows), "Dark", or "Light".</summary>
        public string Theme { get; set; } = "System";

        /// <summary>Persisted main-window width in DIPs (guarded/clamped on use).</summary>
        public double WindowWidth { get; set; } = 1050;

        /// <summary>Persisted main-window height in DIPs (guarded/clamped on use).</summary>
        public double WindowHeight { get; set; } = 720;

        // ── Security credentials ─────────────────────────────────────

        /// <summary>HTB App Token used to download VPN keys. DPAPI-encrypted at rest.</summary>
        public string? HtbApiToken { get; set; }

        // ── AI / LLM configuration ───────────────────────────────────

        /// <summary>
        /// OpenAI-compatible chat-completions endpoint (e.g.
        /// <c>https://api.openai.com/v1/chat/completions</c> or a local
        /// vLLM/Ollama/LM Studio URL). Consumed by the Elastic SIEM AI setup.
        /// </summary>
        public string? LlmChatCompletionsUrl { get; set; }

        /// <summary>LLM API key. DPAPI-encrypted at rest. May be a dummy for local LLMs.</summary>
        public string? LlmApiKey { get; set; }

        /// <summary>Model name sent to the chat-completions endpoint (e.g. <c>gpt-4o</c>).</summary>
        public string? LlmModel { get; set; }

        /// <summary>True when a complete LLM configuration (URL + model; key may be dummy) is present.</summary>
        [JsonIgnore]
        public bool HasLlmConfiguration =>
            !string.IsNullOrWhiteSpace(LlmChatCompletionsUrl)
            && !string.IsNullOrWhiteSpace(LlmModel)
            && !string.IsNullOrWhiteSpace(LlmApiKey);

        // ── Network ──────────────────────────────────────────────────

        /// <summary>DNS resolution mode the wizard pre-fills. Default: host DNS.</summary>
        public DnsMode DnsMode { get; set; } = DnsMode.Host;

        /// <summary>Comma-separated nameserver IPs for <see cref="DnsMode.Custom"/> (e.g. "9.9.9.9,1.1.1.1").</summary>
        public string? CustomNameservers { get; set; }

        // ── VM defaults ──────────────────────────────────────────────

        /// <summary>RDP backend the wizard pre-selects. Default: auto-detect at deploy time.</summary>
        public RdpBackend RdpBackend { get; set; } = RdpBackend.Auto;

        /// <summary>Default VM memory in MB (mirrors <see cref="VmSettings.MemoryInMB"/>).</summary>
        public int DefaultMemoryInMB { get; set; } = 4096;

        /// <summary>Default vCPU count (mirrors <see cref="VmSettings.CPUCount"/>).</summary>
        public int DefaultCpuCount { get; set; } = 2;

        /// <summary>Default disk size in GB (mirrors <see cref="VmSettings.NewDriveSizeInGB"/>).</summary>
        public int DefaultDiskSizeInGB { get; set; } = 150;

        // ── Advanced / machine paths ─────────────────────────────────

        /// <summary>
        /// Optional override for <see cref="AppSettings.QemuImgPath"/>. When null/empty
        /// the AppSettings default applies.
        /// </summary>
        public string? QemuImgPathOverride { get; set; }

        /// <summary>
        /// Optional override for <see cref="AppSettings.ExtractPath"/>. When null/empty
        /// the AppSettings default applies.
        /// </summary>
        public string? ExtractPathOverride { get; set; }

        /// <summary>
        /// Returns a deep copy with the secret fields (HTB token, LLM key) masked,
        /// suitable for logging or display. Non-secret fields are copied verbatim.
        /// </summary>
        public UserPreferences WithMaskedSecrets() => new()
        {
            Theme = Theme,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            HtbApiToken = SecretMask.Mask(HtbApiToken),
            LlmChatCompletionsUrl = LlmChatCompletionsUrl,
            LlmApiKey = SecretMask.Mask(LlmApiKey),
            LlmModel = LlmModel,
            DnsMode = DnsMode,
            CustomNameservers = CustomNameservers,
            RdpBackend = RdpBackend,
            DefaultMemoryInMB = DefaultMemoryInMB,
            DefaultCpuCount = DefaultCpuCount,
            DefaultDiskSizeInGB = DefaultDiskSizeInGB,
            QemuImgPathOverride = QemuImgPathOverride,
            ExtractPathOverride = ExtractPathOverride,
        };
    }
}
