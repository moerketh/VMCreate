using System.IO;
using System.Text.Json;
using Serilog;

namespace VMCreate.Manager
{
    /// <summary>Window and grid layout, restored on the next start. Cosmetic only: any failure falls back to defaults.</summary>
    public sealed class UiSettings
    {
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public bool Maximized { get; set; }
        public double? NavigationWidth { get; set; }
        public double? DetailsWidth { get; set; }
        public List<ColumnSetting> Columns { get; set; } = new();
        public string? SortColumn { get; set; }
        public bool SortDescending { get; set; }

        public sealed class ColumnSetting
        {
            public string Header { get; set; } = "";
            public double Width { get; set; }
            public bool IsStar { get; set; }
            public int DisplayIndex { get; set; }
            public bool Visible { get; set; } = true;
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VMCreate", "Manager", "ui-settings.json");

        public static UiSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new UiSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Ignoring unreadable UI settings {Path}", FilePath);
            }
            return new UiSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Could not save UI settings {Path}", FilePath);
            }
        }
    }
}
