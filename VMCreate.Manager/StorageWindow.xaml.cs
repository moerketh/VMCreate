using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using VMCreate.Manager.HyperV;
using VMCreate.Manager.Inventory;
using VMCreate.Manager.Storage;
using VMCreate.Metadata;

namespace VMCreate.Manager
{
    /// <summary>
    /// Storage overview (drives, per-VM disk use, old checkpoints), unused files
    /// with cleanup, missing files, and what the search skipped.
    /// </summary>
    public partial class StorageWindow
    {
        private readonly IHyperVHost _host;
        private readonly StorageScanner _scanner;
        private readonly OrphanCleanupService _cleanup;
        private readonly IVmMetadataStore _store;
        private readonly IAuditLog _audit;
        private readonly ILogger<StorageWindow> _logger;
        private StorageScan? _scan;
        private IReadOnlyList<OrphanRow> _orphanRows = Array.Empty<OrphanRow>();
        private bool _busy;
        private bool _loadingSettings;

        public StorageWindow(IHyperVHost host, StorageScanner scanner, OrphanCleanupService cleanup, IVmMetadataStore store, IAuditLog audit, ILogger<StorageWindow> logger)
        {
            InitializeComponent();
            _host = host;
            _scanner = scanner;
            _cleanup = cleanup;
            _store = store;
            _audit = audit;
            _logger = logger;
            Loaded += async (_, _) => await ScanAsync();
        }

        // ── scanning ────────────────────────────────────────────────────

        private async Task ScanAsync()
        {
            if (_busy)
                return;
            SetBusy(true);
            try
            {
                var doc = await _store.LoadAsync();
                _loadingSettings = true;
                OldDaysBox.Value = doc.Storage.OldCheckpointDays;
                FoldersList.ItemsSource = doc.Storage.ExtraScanFolders.ToList();
                _loadingSettings = false;

                _scan = await _scanner.ScanAsync(new Progress<string>(text => StatusText.Text = text));
                ShowScan();
                ShowStale(doc, _scan.VmNames.Keys.ToList());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Storage scan failed");
                StatusText.Text = "The scan failed: " + ex.Message;
            }
            finally
            {
                _loadingSettings = false;
                SetBusy(false);
            }
        }

        private void ShowScan()
        {
            if (_scan is not StorageScan scan)
                return;
            int days = (int)(OldDaysBox.Value ?? 30);
            var report = StorageReportBuilder.Build(scan, days, DateTime.Now, StorageReportBuilder.ReadDrive);

            OrphanSummary.Text = $"{report.OrphanCount} item(s), {DisplayFormat.Bytes(report.OrphanBytes)}";
            MissingSummary.Text = report.MissingCount == 0 ? "None" : $"{report.MissingCount} file(s) VMs refer to";
            OldCheckpointSummary.Text = report.OldCheckpoints.Count.ToString();
            OldCheckpointsHeader.Text = $"CHECKPOINTS OLDER THAN {days} DAYS";
            VolumesGrid.ItemsSource = report.Volumes.Select(v => new VolumeRow(v)).ToList();
            VmsGrid.ItemsSource = report.Vms.Select(v => new VmRow(v)).ToList();
            OldCheckpointsGrid.ItemsSource = report.OldCheckpoints;

            _orphanRows = scan.Orphans.Select(o => new OrphanRow(o)).ToList();
            ApplyOrphanFilter();
            MissingGrid.ItemsSource = scan.Missing;
            SkippedGrid.ItemsSource = scan.Skipped
                .Concat(scan.UnreadableFolders.Select(f => new SkippedPath(f, "Could not be read (no access)")))
                .ToList();

            OrphansTab.Header = $"Unused files ({scan.Orphans.Count})";
            MissingTab.Header = $"Missing files ({scan.Missing.Count})";
            SkippedTab.Header = $"Skipped ({scan.Skipped.Count + scan.UnreadableFolders.Count})";
            StatusText.Text = $"Scanned {scan.ScannedAt:HH:mm:ss}: {scan.Orphans.Count} unused item(s), {DisplayFormat.Bytes(scan.OrphanBytes)}; "
                + $"{scan.Missing.Count} missing file(s).";
            FooterText.Text = "Searched: " + string.Join("  ·  ", scan.Roots);
        }

        private void ApplyOrphanFilter()
        {
            string text = OrphanSearch.Text.Trim();
            OrphansGrid.ItemsSource = text.Length == 0
                ? _orphanRows
                : _orphanRows.Where(r => r.Matches(text)).ToList();
            UpdateDeleteButton();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            ScanButton.IsEnabled = !busy;
            UpdateDeleteButton();
            UpdateStaleButton();
        }

        private void UpdateDeleteButton()
            => DeleteButton.IsEnabled = !_busy && SelectedOrphans().Any(r => !r.InUse);

        private List<OrphanRow> SelectedOrphans() => OrphansGrid.SelectedItems.OfType<OrphanRow>().ToList();

        // ── deleting ────────────────────────────────────────────────────

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            var rows = SelectedOrphans().Where(r => !r.InUse).ToList();
            if (rows.Count == 0 || _busy)
                return;
            bool permanently = PermanentBox.IsChecked == true;
            long bytes = rows.Sum(r => r.SizeBytes);
            const int listed = 12;
            string list = string.Join(Environment.NewLine, rows.Take(listed).Select(r => "• " + r.Path))
                + (rows.Count > listed ? $"{Environment.NewLine}… and {rows.Count - listed} more" : "");
            string how = permanently
                ? "They are deleted PERMANENTLY."
                : "Files go to the Recycle Bin (Windows asks first if one is too big for it). Empty folders are removed.";
            if (MessageBox.Show(this,
                    $"Delete {rows.Count} unused item(s), {DisplayFormat.Bytes(bytes)}?{Environment.NewLine}{Environment.NewLine}{list}"
                    + $"{Environment.NewLine}{Environment.NewLine}{how} The host is scanned again first; anything a VM uses by now, "
                    + "or that changed, is left alone.",
                    "Delete unused files", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            SetBusy(true);
            StatusText.Text = $"Deleting {rows.Count} item(s)…";
            try
            {
                var result = await _cleanup.DeleteAsync(rows.Select(r => r.Item).ToList(), permanently);
                string mode = permanently ? "permanently" : "to the Recycle Bin";
                foreach (string path in result.DeletedFiles)
                    await AuditAsync(path, $"{path} ({DisplayFormat.Bytes(rows.FirstOrDefault(r => r.Path == path)?.SizeBytes ?? 0)}) {mode}", null);
                foreach (string folder in result.RemovedFolders)
                    await AuditAsync(folder, $"{folder} (empty folder)", null);
                foreach (var failure in result.FailedFiles)
                    await AuditAsync(failure.Path, failure.Path, failure.Error);

                SetBusy(false);
                await ScanAsync();
                StatusText.Text = $"Deleted {result.DeletedFiles.Count} file(s), {DisplayFormat.Bytes(result.BytesFreed)}, "
                    + $"and {result.RemovedFolders.Count} folder(s)."
                    + (result.Complete ? "" : $" {result.FailedFiles.Count} left alone.");
                if (!result.Complete)
                    MessageBox.Show(this, "Left alone:" + Environment.NewLine + Environment.NewLine
                        + string.Join(Environment.NewLine, result.FailedFiles.Select(f => $"• {f.Path}: {f.Error}")),
                        "Delete unused files", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Orphan cleanup failed");
                StatusText.Text = "Could not delete: " + ex.Message;
                SetBusy(false);
            }
        }

        private async Task AuditAsync(string path, string detail, string? error)
        {
            try
            {
                await _audit.AppendAsync(new AuditEntry
                {
                    Tool = "Manager",
                    Action = AuditActions.FileDelete,
                    Detail = detail,
                    Error = error,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write audit entry for {Path}", path);
            }
        }

        // ── metadata of VMs that no longer exist ─────────────────────────

        private void ShowStale(MetadataDocument doc, IReadOnlyCollection<Guid> existing)
        {
            var rows = doc.FindStaleEntries(existing).Select(v => new StaleRow(v, doc.GetFolderPath(v.FolderId))).ToList();
            StaleGrid.ItemsSource = rows;
            StaleTab.Header = $"Old metadata ({rows.Count})";
            UpdateStaleButton();
        }

        private void StaleGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStaleButton();

        private void UpdateStaleButton() => RemoveStaleButton.IsEnabled = !_busy && StaleGrid.SelectedItems.Count > 0;

        private async void RemoveStale_Click(object sender, RoutedEventArgs e)
        {
            var rows = StaleGrid.SelectedItems.OfType<StaleRow>().ToList();
            if (rows.Count == 0 || _busy)
                return;
            int withCredentials = rows.Count(r => r.HasCredentials);
            string list = string.Join(Environment.NewLine, rows.Take(12).Select(r => "• " + r.Name))
                + (rows.Count > 12 ? $"{Environment.NewLine}… and {rows.Count - 12} more" : "");
            if (MessageBox.Show(this,
                    $"Remove the metadata of {rows.Count} VM(s) that are no longer on this host?{Environment.NewLine}{Environment.NewLine}{list}"
                    + (withCredentials > 0 ? $"{Environment.NewLine}{Environment.NewLine}{withCredentials} of them have saved credentials, which are removed too." : ""),
                    "Remove old metadata", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            SetBusy(true);
            try
            {
                // Checked against the host now, not the scan: a VM imported meanwhile keeps its entry.
                var existing = (await _host.ReadSnapshotAsync()).Vms.Select(v => v.Id).ToHashSet();
                var wanted = rows.Select(r => r.VmId).Where(id => !existing.Contains(id)).ToHashSet();
                var removed = await _store.UpdateAsync(doc =>
                {
                    var gone = doc.Vms.Where(v => wanted.Contains(v.VmId)).ToList();
                    doc.Vms.RemoveAll(v => wanted.Contains(v.VmId));
                    return gone;
                });
                foreach (var entry in removed)
                {
                    try
                    {
                        await _audit.AppendAsync(new AuditEntry
                        {
                            Tool = "Manager",
                            Action = AuditActions.MetadataRemove,
                            VmId = entry.VmId,
                            VmName = entry.LastKnownName,
                            Detail = "VM no longer on this host" + (entry.Credential != null ? "; saved credentials removed" : ""),
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not write audit entry for {VmId}", entry.VmId);
                    }
                }
                var doc = await _store.LoadAsync();
                ShowStale(doc, existing);
                int kept = rows.Count - removed.Count;
                StatusText.Text = $"Removed the metadata of {removed.Count} VM(s)." + (kept > 0 ? $" {kept} kept: they are on the host again." : "");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Removing old metadata failed");
                StatusText.Text = "Could not remove: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ── settings ────────────────────────────────────────────────────

        private async void OldDays_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings || !IsLoaded || OldDaysBox.Value is not double value)
                return;
            int days = Math.Clamp((int)value, 1, 3650);
            await SaveSettingsAsync(s => s.OldCheckpointDays = days);
            ShowScan();
        }

        private async void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Also search this folder for unused virtual disks and ISOs" };
            if (dialog.ShowDialog(this) != true)
                return;
            string folder = dialog.FolderName;
            if (Path.GetPathRoot(folder) is string root && string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)
                && MessageBox.Show(this, $"Search all of {folder}? That can take a while and lists every unused ISO on the drive.",
                    "Add folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            await SaveSettingsAsync(s =>
            {
                if (!s.ExtraScanFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    s.ExtraScanFolders.Add(folder);
            });
            await ScanAsync();
        }

        private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (FoldersList.SelectedItem is not string folder)
                return;
            await SaveSettingsAsync(s => s.ExtraScanFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)));
            await ScanAsync();
        }

        private async Task SaveSettingsAsync(Action<StorageSettings> change)
        {
            try
            {
                await _store.UpdateAsync(doc =>
                {
                    change(doc.Storage);
                    return true;
                });
            }
            catch (Exception ex) when (ex is MetadataStoreException or IOException)
            {
                StatusText.Text = "Could not save the setting: " + ex.Message;
            }
        }

        // ── small actions ───────────────────────────────────────────────

        private void Scan_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

        private void OrphanCard_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = OrphansTab;

        private void MissingCard_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = MissingTab;

        private void OrphanSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded)
                ApplyOrphanFilter();
        }

        private void OrphansGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDeleteButton();

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (OrphansGrid.SelectedItem is not OrphanRow row)
                return;
            string args = row.Item.Kind == OrphanKind.EmptyFolder ? $"\"{row.Path}\"" : $"/select,\"{row.Path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }

        private void CopyPaths_Click(object sender, RoutedEventArgs e)
        {
            var rows = SelectedOrphans();
            if (rows.Count > 0)
                Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(r => r.Path)));
        }

        // ── rows ────────────────────────────────────────────────────────

        private sealed class OrphanRow
        {
            public OrphanRow(OrphanItem item) => Item = item;

            public OrphanItem Item { get; }
            public string Path => Item.Path;
            public long SizeBytes => Item.SizeBytes;
            public DateTime LastWriteUtc => Item.LastWriteUtc;
            public bool InUse => Item.InUse;
            public string Size => Item.Kind == OrphanKind.EmptyFolder ? "" : DisplayFormat.Bytes(Item.SizeBytes);
            public string LastChanged => Item.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            public string Reason => Item.Reason;
            public string? Note => Item.Note;

            public string Type => Item.Kind switch
            {
                OrphanKind.VirtualHardDisk => "Virtual disk",
                OrphanKind.CheckpointDisk => "Checkpoint disk",
                OrphanKind.Iso => "ISO",
                OrphanKind.VmConfiguration => "VM configuration",
                _ => "Empty folder",
            };

            public bool Matches(string text)
                => new[] { Path, Type, Reason, Note }.Any(s => s?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
        }

        private sealed class StaleRow
        {
            private readonly VmMetadata _v;
            public StaleRow(VmMetadata v, string? folder)
            {
                _v = v;
                Folder = folder;
            }

            public Guid VmId => _v.VmId;
            public string Name => _v.LastKnownName ?? "(name not recorded)";
            public DateOnly? LastSeenOn => _v.LastSeenOn;
            public string LastSeen => _v.LastSeenOn?.ToString("yyyy-MM-dd") ?? "Unknown";
            public string? Folder { get; }
            public string? Purpose => _v.Purpose ?? _v.Description;
            public bool HasCredentials => _v.Credential != null;
            public string Credentials => HasCredentials ? "Saved" : "";
        }

        private sealed class VolumeRow
        {
            private readonly VolumeUsage _v;
            public VolumeRow(VolumeUsage v) => _v = v;
            public string Root => _v.Root;
            public string? Label => _v.Label;
            public double? UsedFraction => _v.UsedFraction;
            public double UsedPercent => (_v.UsedFraction ?? 0) * 100;
            public long? FreeBytes => _v.FreeBytes;
            public string Free => _v.FreeBytes is long free && _v.TotalBytes is long total
                ? $"{DisplayFormat.Bytes(free)} of {DisplayFormat.Bytes(total)}"
                : "Not available";
            public long VmBytes => _v.VmBytes;
            public string VmDisks => DisplayFormat.Bytes(_v.VmBytes);
            public long OrphanBytes => _v.OrphanBytes;
            public string Unused => _v.OrphanBytes == 0 ? "" : DisplayFormat.Bytes(_v.OrphanBytes);
        }

        private sealed class VmRow
        {
            private readonly VmStorageUsage _v;
            public VmRow(VmStorageUsage v) => _v = v;
            public string Name => _v.Name;
            public long OwnBytes => _v.OwnBytes;
            public string Own => DisplayFormat.Bytes(_v.OwnBytes);
            public long SharedBytes => _v.SharedBytes;
            public string Shared => _v.SharedBytes == 0 ? "" : DisplayFormat.Bytes(_v.SharedBytes);
            public int Checkpoints => _v.Checkpoints;
            public DateTime? OldestCheckpoint => _v.OldestCheckpoint;
            public string Oldest => _v.OldestCheckpoint?.ToString("yyyy-MM-dd") ?? "";
            public int MissingFiles => _v.MissingFiles;
            public string Missing => _v.MissingFiles == 0 ? "" : _v.MissingFiles.ToString();
        }
    }
}
