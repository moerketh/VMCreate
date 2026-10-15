using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Text.Json;

namespace VMCreate
{
    /// <summary>
    /// Loads and saves <see cref="UserPreferences"/> at
    /// <c>%LocalAppData%\VMCreate\settings.json</c> (sibling of the gallery cache).
    /// Secrets (HTB token, LLM API key) are transparently protected with
    /// <see cref="ISecretProtector"/> (DPAPI by default) — the in-memory model
    /// always holds plaintext, the on-disk file never does.
    /// <para>
    /// Failures are deliberately non-fatal: a corrupt or unreadable file logs a
    /// warning and yields defaults, exactly like <see cref="GalleryCache"/> — a
    /// broken preferences file must never block a deployment, only lose the
    /// pre-fill convenience.
    /// </para>
    /// </summary>
    public interface IUserPreferencesStore
    {
        /// <summary>Path of the settings file (exposed for tests and the Settings window title).</summary>
        string FilePath { get; }

        /// <summary>
        /// Loads preferences from disk. Returns defaults when the file is
        /// missing, unreadable or contains secrets undecryptable by this
        /// machine/user.
        /// </summary>
        UserPreferences Load();

        /// <summary>
        /// Persists preferences atomically (temp file + move). The in-memory
        /// instance is never modified; secrets are protected on the way out.
        /// </summary>
        void Save(UserPreferences preferences);
    }

    public class UserPreferencesStore : IUserPreferencesStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        private readonly ISecretProtector _protector;
        private readonly ILogger<UserPreferencesStore> _logger;
        private readonly string _filePath;

        public UserPreferencesStore(
            ISecretProtector protector,
            ILogger<UserPreferencesStore> logger,
            IOptions<AppSettings>? options = null)
        {
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _filePath = options?.Value.UserPreferencesPath
                        ?? DefaultFilePath();
        }

        public string FilePath => _filePath;

        public static string DefaultFilePath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "settings.json");

        public UserPreferences Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return new UserPreferences();

                var json = File.ReadAllText(_filePath);
                var dto = JsonSerializer.Deserialize<UserPreferencesDto>(json, JsonOptions) ?? new UserPreferencesDto();

                return new UserPreferences
                {
                    Theme = dto.Theme,
                    WindowWidth = dto.WindowWidth,
                    WindowHeight = dto.WindowHeight,
                    HtbApiToken = _protector.Unprotect(dto.HtbApiToken),
                    LlmChatCompletionsUrl = dto.LlmChatCompletionsUrl,
                    LlmApiKey = _protector.Unprotect(dto.LlmApiKey),
                    LlmModel = dto.LlmModel,
                    DnsMode = dto.DnsMode,
                    CustomNameservers = dto.CustomNameservers,
                    RdpBackend = dto.RdpBackend,
                    DefaultMemoryInMB = dto.DefaultMemoryInMB,
                    DefaultCpuCount = dto.DefaultCpuCount,
                    DefaultDiskSizeInGB = dto.DefaultDiskSizeInGB,
                    DefaultConsoleResolution = dto.DefaultConsoleResolution,
                    QemuImgPathOverride = dto.QemuImgPathOverride,
                    ExtractPathOverride = dto.ExtractPathOverride,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read user preferences from {Path}; using defaults.", _filePath);
                return new UserPreferences();
            }
        }

        public void Save(UserPreferences preferences)
        {
            if (preferences == null) throw new ArgumentNullException(nameof(preferences));

            try
            {
                var dto = new UserPreferencesDto
                {
                    Theme = preferences.Theme,
                    WindowWidth = preferences.WindowWidth,
                    WindowHeight = preferences.WindowHeight,
                    HtbApiToken = _protector.Protect(preferences.HtbApiToken),
                    LlmChatCompletionsUrl = preferences.LlmChatCompletionsUrl,
                    LlmApiKey = _protector.Protect(preferences.LlmApiKey),
                    LlmModel = preferences.LlmModel,
                    DnsMode = preferences.DnsMode,
                    CustomNameservers = preferences.CustomNameservers,
                    RdpBackend = preferences.RdpBackend,
                    DefaultMemoryInMB = preferences.DefaultMemoryInMB,
                    DefaultCpuCount = preferences.DefaultCpuCount,
                    DefaultDiskSizeInGB = preferences.DefaultDiskSizeInGB,
                    DefaultConsoleResolution = preferences.DefaultConsoleResolution,
                    QemuImgPathOverride = preferences.QemuImgPathOverride,
                    ExtractPathOverride = preferences.ExtractPathOverride,
                };

                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(dto, JsonOptions));
                File.Move(tempPath, _filePath, overwrite: true);

                _logger.LogInformation("Saved user preferences to {Path}.", _filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write user preferences to {Path}.", _filePath);
            }
        }

        /// <summary>
        /// Wire format. Secrets are already protected strings when serialized.
        /// Kept separate from <see cref="UserPreferences"/> so the public model
        /// can never accidentally round-trip ciphertext into memory properties.
        /// </summary>
        private sealed class UserPreferencesDto
        {
            public string Theme { get; set; } = "System";
            public double WindowWidth { get; set; } = 1050;
            public double WindowHeight { get; set; } = 720;
            public string? HtbApiToken { get; set; }
            public string? LlmChatCompletionsUrl { get; set; }
            public string? LlmApiKey { get; set; }
            public string? LlmModel { get; set; }
            public DnsMode DnsMode { get; set; } = DnsMode.Host;
            public string? CustomNameservers { get; set; }
            public RdpBackend RdpBackend { get; set; } = RdpBackend.Auto;
            public int DefaultMemoryInMB { get; set; } = 4096;
            public int DefaultCpuCount { get; set; } = 2;
            public int DefaultDiskSizeInGB { get; set; } = 150;
            public string DefaultConsoleResolution { get; set; } = ConsoleResolutionValidator.Default;
            public string? QemuImgPathOverride { get; set; }
            public string? ExtractPathOverride { get; set; }
        }
    }
}
