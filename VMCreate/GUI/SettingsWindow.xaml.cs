using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace VMCreate
{
    /// <summary>
    /// Machine-level settings dialog (gear in the wizard title bar). WYSIWYG:
    /// secrets are echoed back into their boxes (PasswordBox shows dots) so
    /// what you see is what gets saved; clearing a box removes the value.
    /// The wizard re-applies saved values to any pages not yet visited.
    /// </summary>
    public partial class SettingsWindow : FluentWindow
    {
        private readonly IUserPreferencesStore _store;
        private readonly ILogger _logger;

        public SettingsWindow(IUserPreferencesStore store, ILoggerFactory loggerFactory)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
                .CreateLogger<SettingsWindow>();
            InitializeComponent();

            Title += $" — {System.IO.Path.GetFileName(_store.FilePath)}";
            LoadIntoUi(_store.Load());
        }

        private void LoadIntoUi(UserPreferences p)
        {
            HtbTokenBox.Password = p.HtbApiToken ?? "";
            LlmUrlBox.Text = p.LlmChatCompletionsUrl ?? "";
            LlmKeyBox.Password = p.LlmApiKey ?? "";
            LlmModelBox.Text = p.LlmModel ?? "";

            DnsHostRadio.IsChecked = p.DnsMode != DnsMode.Custom;
            DnsCustomRadio.IsChecked = p.DnsMode == DnsMode.Custom;
            DnsNameserversBox.Text = p.CustomNameservers ?? "";

            SelectRdpBackend(p.RdpBackend);
            MemoryBox.Text = p.DefaultMemoryInMB.ToString(CultureInfo.InvariantCulture);
            CpuBox.Text = p.DefaultCpuCount.ToString(CultureInfo.InvariantCulture);
            DiskBox.Text = p.DefaultDiskSizeInGB.ToString(CultureInfo.InvariantCulture);

            QemuPathBox.Text = p.QemuImgPathOverride ?? "";
            ExtractPathBox.Text = p.ExtractPathOverride ?? "";
        }

        private void SelectRdpBackend(RdpBackend backend)
        {
            var tag = backend.ToString();
            RdpBackendBox.SelectedItem = RdpBackendBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string)i.Tag, tag, StringComparison.OrdinalIgnoreCase));
        }

        private static RdpBackend ParseRdpBackend(ComboBox? box)
        {
            var tag = (box?.SelectedItem as ComboBoxItem)?.Tag as string;
            return Enum.TryParse<RdpBackend>(tag, ignoreCase: true, out var parsed)
                ? parsed
                : RdpBackend.Auto;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(MemoryBox.Text?.Trim(), out var memory) || memory < 512)
            {
                System.Windows.MessageBox.Show(this, "Memory must be an integer of at least 512 MB.", "Settings",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(CpuBox.Text?.Trim(), out var cpu) || cpu < 1)
            {
                System.Windows.MessageBox.Show(this, "vCPUs must be an integer of at least 1.", "Settings",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(DiskBox.Text?.Trim(), out var disk) || disk < 1)
            {
                System.Windows.MessageBox.Show(this, "Disk size must be an integer of at least 1 GB.", "Settings",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            var p = _store.Load(); // preserve any fields not shown here
            p.HtbApiToken = NullIfEmpty(HtbTokenBox.Password);
            p.LlmChatCompletionsUrl = NullIfEmpty(LlmUrlBox.Text);
            p.LlmApiKey = NullIfEmpty(LlmKeyBox.Password);
            p.LlmModel = NullIfEmpty(LlmModelBox.Text);
            p.DnsMode = DnsCustomRadio.IsChecked == true ? DnsMode.Custom : DnsMode.Host;
            p.CustomNameservers = DnsCustomRadio.IsChecked == true ? NullIfEmpty(DnsNameserversBox.Text) : null;
            p.RdpBackend = ParseRdpBackend(RdpBackendBox);
            p.DefaultMemoryInMB = memory;
            p.DefaultCpuCount = cpu;
            p.DefaultDiskSizeInGB = disk;
            p.QemuImgPathOverride = NullIfEmpty(QemuPathBox.Text);
            p.ExtractPathOverride = NullIfEmpty(ExtractPathBox.Text);

            _store.Save(p);
            _logger.LogInformation("User preferences saved via Settings window (LLM configured: {HasLlm})",
                p.HasLlmConfiguration);

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static string? NullIfEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
