using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;

namespace VMCreate
{
    /// <summary>
    /// User-specific GUI preferences persisted between runs
    /// (theme choice and main window size).
    /// </summary>
    public class UserPreferences
    {
        /// <summary>"System" (default, follows Windows), "Dark", or "Light".</summary>
        public string Theme { get; set; } = "System";

        public double WindowWidth { get; set; } = 1050;
        public double WindowHeight { get; set; } = 720;
    }

    /// <summary>
    /// Loads/saves <see cref="UserPreferences"/> as JSON in %LocalAppData%\VMCreate.
    /// Mirrors the GalleryCache approach; all I/O failures are logged and
    /// treated as "no preferences", never as fatal.
    /// </summary>
    public class UserPreferencesStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        private readonly string _filePath;
        private readonly ILogger _logger;

        public UserPreferencesStore(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _filePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VMCreate", "preferences.json");
        }

        public UserPreferences Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return new UserPreferences();

                var prefs = JsonSerializer.Deserialize<UserPreferences>(
                    File.ReadAllText(_filePath), JsonOptions);
                return prefs ?? new UserPreferences();
            }
            catch (Exception ex)
            {
                // Corrupt/migrated file: start from defaults rather than crashing the wizard.
                _logger.LogWarning(ex, "Failed to load user preferences; using defaults.");
                return new UserPreferences();
            }
        }

        public void Save(UserPreferences prefs)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(prefs, JsonOptions));
                _logger.LogInformation("UserPreferences saved to {Path}", _filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save user preferences to {Path}", _filePath);
            }
        }
    }
}
