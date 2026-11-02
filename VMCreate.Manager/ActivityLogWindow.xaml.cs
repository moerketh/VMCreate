using System.Diagnostics;
using System.IO;
using System.Windows;
using VMCreate.Manager.ViewModels;
using VMCreate.Metadata;

namespace VMCreate.Manager
{
    /// <summary>Read-only view of the append-only audit log, newest first.</summary>
    public partial class ActivityLogWindow
    {
        private readonly IAuditLog _audit;
        private IReadOnlyList<AuditEntryView> _all = Array.Empty<AuditEntryView>();

        public ActivityLogWindow(IAuditLog audit)
        {
            InitializeComponent();
            _audit = audit;
            Loaded += async (_, _) => await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            try
            {
                _all = (await _audit.ReadAsync()).Select(e => new AuditEntryView(e)).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _all = Array.Empty<AuditEntryView>();
                FooterText.Text = "Could not read the log: " + ex.Message;
                return;
            }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string text = SearchBox.Text.Trim();
            var shown = _all.Where(e => (FailedOnly.IsChecked != true || e.Failed) && (text.Length == 0 || e.Matches(text))).ToList();
            EntriesGrid.ItemsSource = shown;
            FooterText.Text = $"{shown.Count} of {_all.Count} entries  ·  {_audit.FilePath}";
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
                ApplyFilter();
        }

        private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

        private void ShowFile_Click(object sender, RoutedEventArgs e)
        {
            string target = File.Exists(_audit.FilePath) ? $"/select,\"{_audit.FilePath}\"" : $"\"{Path.GetDirectoryName(_audit.FilePath)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
    }
}
