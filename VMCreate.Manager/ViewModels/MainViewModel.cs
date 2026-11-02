using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using VMCreate.Manager.Archive;
using VMCreate.Manager.Deletion;
using VMCreate.Manager.HyperV;
using VMCreate.Manager.Inventory;
using VMCreate.Manager.Metadata;
using VMCreate.Manager.Ssh;
using VMCreate.Metadata;

namespace VMCreate.Manager.ViewModels
{
    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan ThumbnailInterval = TimeSpan.FromSeconds(2);
        private const int ThumbnailMaxWidth = 640;
        private const int ThumbnailMaxHeight = 480;

        private readonly IVmInventoryService _inventory;
        private readonly IHyperVHost _host;
        private readonly JsonVmMetadataStore _store;
        private readonly VmMetadataSync _sync;
        private readonly IAuditLog _audit;
        private readonly IDialogService _dialogs;
        private readonly VmDeletionService _deletion;
        private readonly FolderArchiver _archiver;
        private CancellationTokenSource? _archiveCts;
        private readonly ILogger<MainViewModel> _logger;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _debounceTimer;
        private readonly DispatcherTimer _liveTimer;
        private readonly DispatcherTimer _thumbnailTimer;
        private bool _thumbnailBusy;
        private ImageSource? _thumbnail;
        private string? _thumbnailMessage;
        private readonly Dictionary<Guid, VmItemViewModel> _itemsById = new();
        private IDisposable? _hostWatch;
        private MetadataDocument _doc = new();
        private string _folderSignature = "";
        private bool _refreshing;
        private bool _refreshPending;

        private NavNodeViewModel? _selectedNode;
        private string _searchText = "";
        private VmItemViewModel? _selectedVm;
        private VmEditorViewModel? _editor;
        private string? _errorMessage;
        private string? _statusMessage;
        private int _legacyCredentialCount;
        private int _expiredCount;
        private bool _isLoading = true;
        private IReadOnlyList<AuditEntryView> _history = Array.Empty<AuditEntryView>();

        public MainViewModel(
            IVmInventoryService inventory,
            IHyperVHost host,
            JsonVmMetadataStore store,
            VmMetadataSync sync,
            IAuditLog audit,
            IDialogService dialogs,
            VmDeletionService deletion,
            FolderArchiver archiver,
            ILogger<MainViewModel> logger)
        {
            _inventory = inventory;
            _host = host;
            _store = store;
            _sync = sync;
            _audit = audit;
            _dialogs = dialogs;
            _deletion = deletion;
            _archiver = archiver;
            _logger = logger;
            _dispatcher = Dispatcher.CurrentDispatcher;

            VmsView = CollectionViewSource.GetDefaultView(Vms);
            VmsView.Filter = o => o is VmItemViewModel vm && PassesFilter(vm);

            _debounceTimer = new DispatcherTimer { Interval = ChangeDebounce };
            _debounceTimer.Tick += async (_, _) => { _debounceTimer.Stop(); await RefreshAsync(); };
            _liveTimer = new DispatcherTimer { Interval = LiveRefreshInterval };
            _liveTimer.Tick += async (_, _) => { if (Vms.Any(v => v.Summary.IsRunning)) await RefreshAsync(); };
            _thumbnailTimer = new DispatcherTimer { Interval = ThumbnailInterval };
            _thumbnailTimer.Tick += async (_, _) => await UpdateThumbnailAsync();

            RefreshCommand = new AsyncRelayCommand(RefreshAsync);
            StartCommand = new AsyncRelayCommand(StartAsync,
                () => SelectedVms.Any(v => !v.IsTemplate && v.State is VmState.Off or VmState.Saved));
            ShutdownCommand = new AsyncRelayCommand(() => ForSelected("shut down", AuditActions.VmShutdown, id => _host.ShutdownAsync(id, force: false)),
                () => SelectedVms.Any(v => v.State == VmState.Running));
            TurnOffCommand = new AsyncRelayCommand(TurnOffAsync,
                () => SelectedVms.Any(v => v.State is VmState.Running or VmState.Paused or VmState.Saved));
            SaveStateCommand = new AsyncRelayCommand(() => ForSelected("save", AuditActions.VmSave, id => _host.RequestStateChangeAsync(id, RequestedVmState.Saved)),
                () => SelectedVms.Any(v => v.State is VmState.Running or VmState.Paused));
            PauseCommand = new AsyncRelayCommand(() => ForSelected("pause", AuditActions.VmPause, id => _host.RequestStateChangeAsync(id, RequestedVmState.Paused)),
                () => SelectedVms.Any(v => v.State == VmState.Running));
            ResumeCommand = new AsyncRelayCommand(() => ForSelected("resume", AuditActions.VmResume, id => _host.RequestStateChangeAsync(id, RequestedVmState.Running)),
                () => SelectedVms.Any(v => v.State == VmState.Paused));
            ConnectCommand = new RelayCommand(Connect, () => SelectedVm != null);
            CopyIpCommand = new RelayCommand(() => Clipboard.SetText(SelectedVm!.PrimaryIp!), () => SelectedVm?.PrimaryIp != null);
            SshCommand = new AsyncRelayCommand(() => OpenSshAsync(null), () => SelectedVm?.Summary.IsRunning == true);
            SshNetworkCommand = new AsyncRelayCommand(() => OpenSshAsync(SshTransport.Network), () => SelectedVm?.PrimaryIp != null);
            SshVsockCommand = new AsyncRelayCommand(() => OpenSshAsync(SshTransport.Vsock), () => SelectedVm?.Summary.IsRunning == true);
            NewLinkedCloneCommand = new AsyncRelayCommand(NewLinkedCloneAsync,
                () => SelectedVms.Count == 1 && SelectedVm?.IsTemplate == true && SelectedVm.State == VmState.Off);
            MarkTemplateCommand = new AsyncRelayCommand(MarkTemplateAsync,
                () => SelectedVms.Count == 1 && SelectedVm?.IsTemplate == false && SelectedVm.TemplateId == null);
            DeleteVmCommand = new AsyncRelayCommand(DeleteVmAsync, () => SelectedVms.Count == 1);
            UnmarkTemplateCommand = new AsyncRelayCommand(UnmarkTemplateAsync,
                () => SelectedVms.Count == 1 && SelectedVm?.IsTemplate == true);
            IsolateNetworkCommand = new AsyncRelayCommand(IsolateNetworkAsync,
                () => SelectedVms.Any(v => v.Exposure != NetworkExposure.None));
            RestoreNetworkCommand = new AsyncRelayCommand(RestoreNetworkAsync,
                () => SelectedVms.Any(v => v.CanReconnectNetwork));
            ConnectToSwitchCommand = new RelayCommand<SwitchOption>(
                sw => _ = ConnectToSwitchAsync(sw!),
                sw => sw != null && SelectedVms.Count > 0);
            MoveSelectedToFolderCommand = new RelayCommand<FolderOption>(
                f => _ = MoveVmsToFolderAsync(SelectedVms.Select(v => v.Id).ToList(), f?.Id),
                f => f != null && SelectedVms.Count > 0);

            NewFolderCommand = new AsyncRelayCommand(NewFolderAsync);
            RenameFolderCommand = new AsyncRelayCommand(RenameFolderAsync, () => SelectedNode?.Kind == NavNodeKind.Folder);
            DeleteFolderCommand = new AsyncRelayCommand(DeleteFolderAsync, () => SelectedNode?.Kind == NavNodeKind.Folder);
            SetFolderExpiryCommand = new AsyncRelayCommand(SetFolderExpiryAsync, () => SelectedNode?.Kind == NavNodeKind.Folder);
            ArchiveFolderCommand = new AsyncRelayCommand(ArchiveFolderAsync, () => SelectedNode?.Kind == NavNodeKind.Folder && !IsArchiving);
            CancelArchiveCommand = new RelayCommand(() => _archiveCts?.Cancel(), () => _archiveCts is { IsCancellationRequested: false });
            ShowExpiredCommand = new RelayCommand(ShowExpired);

            SaveEditorCommand = new AsyncRelayCommand(SaveEditorAsync, () => Editor?.IsDirty == true);
            RevertEditorCommand = new RelayCommand(() => LoadEditor(force: true), () => Editor?.IsDirty == true);
            RevealPasswordCommand = new RelayCommand(RevealPassword, () => Editor?.HasPassword == true);
            CopyPasswordCommand = new RelayCommand(CopyPassword, () => Editor?.HasPassword == true);
            MigrateLegacyCredentialsCommand = new AsyncRelayCommand(MigrateLegacyCredentialsAsync, () => LegacyCredentialCount > 0);
            DismissErrorCommand = new RelayCommand(() => ErrorMessage = null);
        }

        // ── bindable state ────────────────────────────────────────────────

        public ObservableCollection<VmItemViewModel> Vms { get; } = new();
        public ICollectionView VmsView { get; }
        public ObservableCollection<NavNodeViewModel> Navigation { get; } = new();
        public ObservableCollection<FolderOption> FolderOptions { get; } = new();
        public ObservableCollection<SwitchOption> SwitchOptions { get; } = new();

        /// <summary>Set by the view from DataGrid.SelectedItems (not bindable in WPF).</summary>
        public IReadOnlyList<VmItemViewModel> SelectedVms { get; private set; } = Array.Empty<VmItemViewModel>();

        public NavNodeViewModel? SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (SetProperty(ref _selectedNode, value))
                {
                    UpdateGrouping();
                    VmsView.Refresh();
                }
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                    VmsView.Refresh();
            }
        }

        public VmItemViewModel? SelectedVm
        {
            get => _selectedVm;
            private set
            {
                if (SetProperty(ref _selectedVm, value))
                {
                    LoadEditor(force: true);
                    SelectedVmHistory = Array.Empty<AuditEntryView>();
                    _ = LoadHistoryAsync();
                    Thumbnail = null;
                    ThumbnailMessage = null;
                    _ = UpdateThumbnailAsync();
                }
            }
        }

        /// <summary>Live console screenshot of the selected VM, like the Hyper-V Manager preview.</summary>
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            private set => SetProperty(ref _thumbnail, value);
        }

        /// <summary>Shown instead of the screenshot when there is none (VM off, saved, booting).</summary>
        public string? ThumbnailMessage
        {
            get => _thumbnailMessage;
            private set => SetProperty(ref _thumbnailMessage, value);
        }

        /// <summary>Latest audit entries of the selected VM, newest first.</summary>
        public IReadOnlyList<AuditEntryView> SelectedVmHistory
        {
            get => _history;
            private set => SetProperty(ref _history, value);
        }

        public string AuditFilePath => _audit.FilePath;

        public VmEditorViewModel? Editor
        {
            get => _editor;
            private set => SetProperty(ref _editor, value);
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (SetProperty(ref _errorMessage, value))
                    OnPropertyChanged(nameof(IsErrorOpen));
            }
        }

        /// <summary>Bound to the error InfoBar; closing it clears the message.</summary>
        public bool IsErrorOpen
        {
            get => _errorMessage != null;
            set
            {
                if (!value)
                    ErrorMessage = null;
            }
        }

        public string? StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        /// <summary>Shows the Cancel button next to the archive progress.</summary>
        public bool IsArchiving => _archiveCts != null;

        /// <summary>VMs past their (own or folder) expiry date; drives the reminder banner.</summary>
        public int ExpiredCount
        {
            get => _expiredCount;
            private set => SetProperty(ref _expiredCount, value);
        }

        public int LegacyCredentialCount
        {
            get => _legacyCredentialCount;
            private set => SetProperty(ref _legacyCredentialCount, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        public string MetadataFilePath => _store.FilePath;

        public ICommand RefreshCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand ShutdownCommand { get; }
        public ICommand TurnOffCommand { get; }
        public ICommand SaveStateCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand ResumeCommand { get; }
        public ICommand ConnectCommand { get; }
        public ICommand CopyIpCommand { get; }
        public ICommand SshCommand { get; }
        public ICommand SshNetworkCommand { get; }
        public ICommand SshVsockCommand { get; }
        public ICommand MoveSelectedToFolderCommand { get; }
        public ICommand IsolateNetworkCommand { get; }
        public ICommand NewLinkedCloneCommand { get; }
        public ICommand MarkTemplateCommand { get; }
        public ICommand UnmarkTemplateCommand { get; }
        public ICommand DeleteVmCommand { get; }
        public ICommand RestoreNetworkCommand { get; }
        public ICommand ConnectToSwitchCommand { get; }
        public ICommand NewFolderCommand { get; }
        public ICommand RenameFolderCommand { get; }
        public ICommand DeleteFolderCommand { get; }
        public ICommand SetFolderExpiryCommand { get; }
        public ICommand ArchiveFolderCommand { get; }
        public ICommand CancelArchiveCommand { get; }
        public ICommand ShowExpiredCommand { get; }
        public ICommand SaveEditorCommand { get; }
        public ICommand RevertEditorCommand { get; }
        public ICommand RevealPasswordCommand { get; }
        public ICommand CopyPasswordCommand { get; }
        public ICommand MigrateLegacyCredentialsCommand { get; }
        public ICommand DismissErrorCommand { get; }

        // ── lifecycle ────────────────────────────────────────────────────

        public async Task InitializeAsync()
        {
            await RefreshAsync();
            try
            {
                _hostWatch = _inventory.WatchChanges(ScheduleRefresh);
            }
            catch (Exception ex)
            {
                // Live updates are a convenience: without them, Refresh and the timer still work.
                _logger.LogWarning(ex, "Could not subscribe to Hyper-V change events");
            }
            _store.Changed += (_, _) => ScheduleRefresh();
            _store.StartWatching();
            _liveTimer.Start();
        }

        public void Dispose()
        {
            _hostWatch?.Dispose();
            _liveTimer.Stop();
            _debounceTimer.Stop();
            _thumbnailTimer.Stop();
        }

        private void ScheduleRefresh()
            => _dispatcher.BeginInvoke(() => { _debounceTimer.Stop(); _debounceTimer.Start(); });

        public async Task RefreshAsync()
        {
            if (_refreshing)
            {
                _refreshPending = true;
                return;
            }
            _refreshing = true;
            try
            {
                do
                {
                    _refreshPending = false;
                    var vms = await _inventory.GetAllAsync();
                    var changes = await _sync.ReconcileAsync(vms);
                    if (changes.Count > 0)
                        await ReportSyncAsync(changes);
                    _doc = await _store.LoadAsync();
                    Apply(vms);
                } while (_refreshPending);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refresh failed");
                ErrorMessage = ex.Message;
            }
            finally
            {
                _refreshing = false;
                IsLoading = false;
            }
        }

        private void Apply(IReadOnlyList<VmSummary> vms)
        {
            var seen = new HashSet<Guid>();
            foreach (var summary in vms)
            {
                seen.Add(summary.Id);
                var meta = _doc.FindVm(summary.Id);
                var folder = VmFolderContext.From(_doc, meta);
                if (_itemsById.TryGetValue(summary.Id, out var item))
                {
                    item.Update(summary, meta, folder);
                }
                else
                {
                    item = new VmItemViewModel(summary, meta, folder);
                    _itemsById[summary.Id] = item;
                    Vms.Add(item);
                }
            }
            foreach (var gone in _itemsById.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                Vms.Remove(_itemsById[gone]);
                _itemsById.Remove(gone);
            }

            RebuildNavigationIfNeeded();
            UpdateSwitchOptions();
            UpdateCounts();
            LegacyCredentialCount = vms.Count(VmMetadataSync.HasLegacyCredentials);
            ExpiredCount = Vms.Count(v => v.IsExpired);
            VmsView.Refresh();

            if (SelectedVm != null && !_itemsById.ContainsKey(SelectedVm.Id))
                SetSelection(Array.Empty<VmItemViewModel>());
            else
                LoadEditor(force: false);
        }

        // ── folder grouping ──────────────────────────────────────────────

        private static readonly PropertyGroupDescription FolderGrouping =
            new(nameof(VmItemViewModel.GroupKey)) { CustomSort = new FolderGroupComparer() };

        /// <summary>"All virtual machines" shows VMs under their folders; every other view stays a flat list.</summary>
        private void UpdateGrouping()
        {
            bool grouped = SelectedNode?.Kind == NavNodeKind.All;
            if (grouped == VmsView.GroupDescriptions.Contains(FolderGrouping))
                return;
            if (grouped)
                VmsView.GroupDescriptions.Add(FolderGrouping);
            else
                VmsView.GroupDescriptions.Clear();
        }

        /// <summary>Folders alphabetically by path (a parent directly before its subfolders), unfiled last.</summary>
        private sealed class FolderGroupComparer : System.Collections.IComparer
        {
            public int Compare(object? x, object? y)
            {
                string a = (x as CollectionViewGroup)?.Name as string ?? "";
                string b = (y as CollectionViewGroup)?.Name as string ?? "";
                if (a.Length == 0 || b.Length == 0)
                    return (a.Length == 0).CompareTo(b.Length == 0);
                // Per segment, so "Lab/X" stays right after "Lab" even when a sibling like "Lab 2" exists.
                string[] pa = a.Split(MetadataOperations.FolderPathSeparator);
                string[] pb = b.Split(MetadataOperations.FolderPathSeparator);
                for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
                {
                    int c = StringComparer.CurrentCultureIgnoreCase.Compare(pa[i], pb[i]);
                    if (c != 0)
                        return c;
                }
                return pa.Length.CompareTo(pb.Length);
            }
        }

        // ── navigation tree ──────────────────────────────────────────────

        private void RebuildNavigationIfNeeded()
        {
            string signature = string.Join("|", _doc.Folders.OrderBy(f => f.Id).Select(f => $"{f.Id}:{f.ParentId}:{f.Name}:{f.Color}:{f.ExpiresOn}"))
                + "#" + string.Join("|", _doc.SmartFolders.Select(s => s.Name));
            if (signature == _folderSignature && Navigation.Count > 0)
                return;
            _folderSignature = signature;

            string? selectedKey = SelectedNode?.Key ?? NavNodeKind.All.ToString();
            var expanded = Navigation.SelectMany(n => n.SelfAndDescendants()).Where(n => !n.IsExpanded).Select(n => n.Key).ToHashSet();

            Navigation.Clear();
            var all = new NavNodeViewModel(NavNodeKind.All, "All virtual machines", "Server24");
            AddFolderChildren(all, null);
            Navigation.Add(all);
            Navigation.Add(new NavNodeViewModel(NavNodeKind.Unfiled, "Not in a folder", "FolderProhibited24"));
            Navigation.Add(new NavNodeViewModel(NavNodeKind.Smart, "Running", "Play24", filter: new SmartFolderFilter { States = { "Running" } }));
            Navigation.Add(new NavNodeViewModel(NavNodeKind.Smart, "Protected", "ShieldLock24", filter: new SmartFolderFilter { ProtectedOnly = true }));
            Navigation.Add(new NavNodeViewModel(NavNodeKind.Smart, "Expired", "CalendarCancel24", filter: new SmartFolderFilter { ExpiredOnly = true }));
            foreach (var smart in _doc.SmartFolders)
                Navigation.Add(new NavNodeViewModel(NavNodeKind.Smart, smart.Name, "Filter24", filter: smart.Filter));

            NavNodeViewModel? toSelect = null;
            foreach (var node in Navigation.SelectMany(n => n.SelfAndDescendants()))
            {
                if (expanded.Contains(node.Key))
                    node.IsExpanded = false;
                if (node.Key == selectedKey)
                    toSelect = node;
            }
            toSelect ??= all;
            toSelect.IsSelected = true;
            SelectedNode = toSelect;

            FolderOptions.Clear();
            FolderOptions.Add(new FolderOption(null, "(none)"));
            foreach (var f in _doc.Folders.Select(f => new FolderOption(f.Id, _doc.GetFolderPath(f.Id)!)).OrderBy(o => o.Path, StringComparer.OrdinalIgnoreCase))
                FolderOptions.Add(f);
        }

        private void AddFolderChildren(NavNodeViewModel parent, Guid? parentId)
        {
            foreach (var folder in _doc.Folders.Where(f => f.ParentId == parentId).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var node = new NavNodeViewModel(NavNodeKind.Folder, folder.Name, "Folder24", folder.Id,
                    color: _doc.GetFolderColor(folder.Id), expiresOn: folder.ExpiresOn);
                AddFolderChildren(node, folder.Id);
                parent.Children.Add(node);
            }
        }

        private void UpdateCounts()
        {
            foreach (var node in Navigation.SelectMany(n => n.SelfAndDescendants()))
                node.Count = Vms.Count(vm => MatchesNode(node, vm));
        }

        private bool PassesFilter(VmItemViewModel vm)
        {
            if (SelectedNode != null && !MatchesNode(SelectedNode, vm))
                return false;
            if (string.IsNullOrWhiteSpace(SearchText))
                return true;
            return MetadataOperations.MatchesText(SearchText, vm.Name, vm.Metadata)
                || vm.Summary.IpAddresses.Any(ip => ip.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private bool MatchesNode(NavNodeViewModel node, VmItemViewModel vm) => node.Kind switch
        {
            NavNodeKind.All => true,
            NavNodeKind.Unfiled => vm.FolderId == null || _doc.FindFolder(vm.FolderId.Value) == null,
            // A folder shows its own VMs plus those in its subfolders.
            NavNodeKind.Folder => vm.FolderId is Guid f && node.SelfAndDescendants().Any(n => n.FolderId == f),
            NavNodeKind.Smart => node.Filter!.Matches(vm.Name, vm.State.ToString(), vm.Metadata, DateOnly.FromDateTime(DateTime.Today), vm.EffectiveExpiresOn),
            _ => false,
        };

        // ── selection / editor ───────────────────────────────────────────

        public void SetSelection(IReadOnlyList<VmItemViewModel> selected)
        {
            SelectedVms = selected;
            SelectedVm = selected.Count == 1 ? selected[0] : null;
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task UpdateThumbnailAsync()
        {
            var vm = SelectedVm;
            if (vm == null)
            {
                _thumbnailTimer.Stop();
                Thumbnail = null;
                ThumbnailMessage = null;
                return;
            }
            _thumbnailTimer.Start();
            if (_thumbnailBusy)
                return;

            if (vm.State == VmState.Off)
            {
                Thumbnail = null;
                ThumbnailMessage = "Turned off";
                return;
            }

            _thumbnailBusy = true;
            try
            {
                var shot = await _host.GetThumbnailAsync(vm.Id, ThumbnailMaxWidth, ThumbnailMaxHeight);
                if (SelectedVm != vm)
                    return; // selection moved on while capturing
                if (shot == null)
                {
                    Thumbnail = null;
                    ThumbnailMessage = vm.State == VmState.Saved ? "Saved — no preview available" : "No display output";
                    return;
                }
                var bitmap = BitmapSource.Create(shot.Width, shot.Height, 96, 96, PixelFormats.Bgr565, null, shot.Pixels, shot.Width * 2);
                bitmap.Freeze();
                Thumbnail = bitmap;
                ThumbnailMessage = null;
            }
            catch (Exception ex)
            {
                // A preview failure (VM deleted, transient WMI error) is not worth an error banner.
                _logger.LogDebug(ex, "Thumbnail capture failed for {Vm}", vm.Name);
                if (SelectedVm == vm)
                {
                    Thumbnail = null;
                    ThumbnailMessage = "Preview unavailable";
                }
            }
            finally
            {
                _thumbnailBusy = false;
            }
        }

        private void LoadEditor(bool force)
        {
            if (SelectedVm == null)
            {
                Editor = null;
                return;
            }
            // Never overwrite edits in progress with refreshed data.
            if (!force && Editor != null && Editor.VmId == SelectedVm.Id && Editor.IsDirty)
                return;
            Editor = new VmEditorViewModel(SelectedVm) { LineageText = DescribeLineage(SelectedVm) };
        }

        private async Task SaveEditorAsync()
        {
            var editor = Editor;
            if (editor == null)
                return;

            if (editor.WasProtected && !editor.Protected
                && !_dialogs.ConfirmByTyping("Remove delete protection",
                    $"Type the VM name to remove delete protection from '{editor.Item.Name}'.", editor.Item.Name))
            {
                editor.Protected = true;
                return;
            }

            string oldName = editor.Item.Name;
            string newName = editor.Name.Trim();
            var changes = DescribeEditorChanges(editor, newName);
            string? error = null;
            try
            {
                if (newName.Length > 0 && newName != oldName)
                    await _host.RenameAsync(editor.VmId, newName);

                await _sync.UpdateVmAsync(editor.VmId, (_, vm) => editor.ApplyTo(vm));
                editor.MarkClean();
                StatusMessage = $"Saved '{newName}'.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving VM {VmId} failed", editor.VmId);
                ErrorMessage = error = ex.Message;
            }
            foreach (var (action, detail) in changes)
                await AuditAsync(action, editor.VmId, oldName, detail, error);
            if (error == null)
            {
                await RefreshAsync();
                LoadEditor(force: true);
            }
        }

        /// <summary>The audit-worthy parts of an editor save (purpose, tags and description are not).</summary>
        private List<(string Action, string? Detail)> DescribeEditorChanges(VmEditorViewModel editor, string newName)
        {
            var changes = new List<(string, string?)>();
            if (newName.Length > 0 && newName != editor.Item.Name)
                changes.Add((AuditActions.VmRename, $"{editor.Item.Name} → {newName}"));
            if (editor.Protected != editor.WasProtected)
                changes.Add((editor.Protected ? AuditActions.VmProtect : AuditActions.VmUnprotect, null));
            DateOnly? expires = editor.ExpiresOn is DateTime d ? DateOnly.FromDateTime(d) : null;
            if (expires != editor.Item.ExpiresOn)
                changes.Add((AuditActions.VmExpiry, expires?.ToString("yyyy-MM-dd") ?? "cleared"));
            if (editor.FolderId != editor.Item.FolderId)
                changes.Add((AuditActions.VmMove, $"{FolderLabel(editor.Item.FolderId)} → {FolderLabel(editor.FolderId)}"));
            return changes;
        }

        private string FolderLabel(Guid? folderId) => _doc.GetFolderPath(folderId) ?? "(no folder)";

        // ── audit ────────────────────────────────────────────────────────

        /// <summary>Best effort: a failing audit write is logged but never blocks the action itself.</summary>
        /// <summary>Status line plus activity log entries for what syncing with Notes did.</summary>
        private async Task ReportSyncAsync(IReadOnlyList<VmSyncChange> changes)
        {
            int adopted = 0, taken = 0;
            foreach (var change in changes)
            {
                var r = change.Result;
                switch (r.Action)
                {
                    case NotesSyncAction.Adopted:
                        adopted++;
                        break;
                    case NotesSyncAction.TookFromNotes when r.ChangedFields.Count > 0:
                        taken++;
                        string kept = r.NeedsPush && !r.ChangedFields.Contains("protected")
                            ? "; protection stays on (remove it in the Manager)" : "";
                        await AuditAsync(AuditActions.MetadataExternalEdit, change.VmId, change.VmName,
                            $"Notes edited outside the Manager, taken over: {string.Join(", ", r.ChangedFields)}{kept}", change.PushError);
                        break;
                    case NotesSyncAction.RestoreBlock:
                        await AuditAsync(AuditActions.MetadataExternalEdit, change.VmId, change.VmName,
                            "The [vmcreate] block was removed from Notes outside the Manager; written back", change.PushError);
                        break;
                    case NotesSyncAction.Conflict:
                        await AuditAsync(AuditActions.MetadataConflict, change.VmId, change.VmName,
                            $"Notes and the Manager both changed; the Manager's copy was kept. Replaced block: {r.OverwrittenBlock}", change.PushError);
                        break;
                }
            }
            var parts = new List<string>();
            if (adopted > 0)
                parts.Add($"restored metadata of {adopted} VM(s) from their Notes");
            if (taken > 0)
                parts.Add($"took Notes edits of {taken} VM(s) made outside the Manager");
            if (parts.Count > 0)
                StatusMessage = char.ToUpperInvariant(parts[0][0]) + string.Join("; ", parts)[1..] + ".";
        }

        private async Task AuditAsync(string action, Guid? vmId, string? vmName, string? detail = null, string? error = null)
        {
            try
            {
                await _audit.AppendAsync(new AuditEntry
                {
                    Tool = "Manager",
                    Action = action,
                    VmId = vmId,
                    VmName = vmName,
                    Detail = detail,
                    Error = error,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write audit entry {Action}", action);
            }
            if (vmId != null && vmId == SelectedVm?.Id)
                await LoadHistoryAsync();
        }

        private async Task LoadHistoryAsync()
        {
            var id = SelectedVm?.Id;
            if (id == null)
                return;
            try
            {
                var entries = await _audit.ReadAsync(id, max: 20);
                if (SelectedVm?.Id == id)
                    SelectedVmHistory = entries.Select(e => new AuditEntryView(e)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the audit log");
            }
        }

        private void RevealPassword()
        {
            var cred = Editor?.Item.Metadata?.Credential;
            if (cred == null)
                return;
            try
            {
                Editor!.RevealedPassword = Editor.RevealedPassword == null ? _sync.RevealPassword(cred) : null;
            }
            catch (Exception ex)
            {
                ErrorMessage = "Could not decrypt the password (stored by another Windows user?): " + ex.Message;
            }
        }

        private void CopyPassword()
        {
            var cred = Editor?.Item.Metadata?.Credential;
            if (cred == null)
                return;
            try
            {
                if (_sync.RevealPassword(cred) is string pw)
                {
                    Clipboard.SetText(pw);
                    StatusMessage = "Password copied to the clipboard.";
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = "Could not decrypt the password: " + ex.Message;
            }
        }

        private async Task MigrateLegacyCredentialsAsync()
        {
            int moved = 0;
            var errors = new List<string>();
            foreach (var vm in Vms.Where(v => VmMetadataSync.HasLegacyCredentials(v.Summary)).ToList())
            {
                try
                {
                    if (await _sync.MigrateLegacyCredentialsAsync(vm.Id))
                        moved++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Credential migration failed for {Vm}", vm.Name);
                    errors.Add($"{vm.Name}: {ex.Message}");
                }
            }
            StatusMessage = $"Moved credentials of {moved} VM(s) into the encrypted store.";
            if (errors.Count > 0)
                ErrorMessage = string.Join(Environment.NewLine, errors);
            await RefreshAsync();
        }

        // ── VM actions ───────────────────────────────────────────────────

        private async Task TurnOffAsync()
        {
            var names = string.Join(", ", SelectedVms.Select(v => v.Name));
            if (!_dialogs.Confirm("Turn off", $"Turning off is like pulling the power cord; unsaved guest data is lost.\n\nTurn off {names}?"))
                return;
            await ForSelected("turn off", AuditActions.VmTurnOff, id => _host.RequestStateChangeAsync(id, RequestedVmState.Off));
        }

        private async Task ForSelected(string verb, string auditAction, Func<Guid, Task> action)
        {
            var targets = SelectedVms.ToList();
            var errors = new List<string>();
            await Task.WhenAll(targets.Select(async vm =>
            {
                string? error = null;
                try
                {
                    await action(vm.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to {Verb} {Vm}", verb, vm.Name);
                    error = ex.Message;
                    lock (errors)
                        errors.Add($"{vm.Name}: {ex.Message}");
                }
                await AuditAsync(auditAction, vm.Id, vm.Name, error: error);
            }));
            ErrorMessage = errors.Count == 0 ? null : $"Could not {verb}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}";
            await RefreshAsync();
        }

        private void Connect()
        {
            var vm = SelectedVm;
            if (vm == null)
                return;
            try
            {
                Process.Start(new ProcessStartInfo("vmconnect.exe", $"localhost -G {vm.Id:D}") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ErrorMessage = "Could not start vmconnect.exe: " + ex.Message;
            }
        }

        /// <summary>
        /// Opens ssh in a new console window. The user name comes from the
        /// stored credential; when there is none the user is asked once and the
        /// answer is remembered (user name only, no password).
        /// </summary>
        private async Task OpenSshAsync(SshTransport? transport)
        {
            var vm = SelectedVm;
            if (vm == null)
                return;

            string? user = vm.Metadata?.Credential?.Username;
            if (string.IsNullOrWhiteSpace(user))
            {
                user = _dialogs.Prompt("SSH", $"User name for '{vm.Name}':", "");
                if (string.IsNullOrWhiteSpace(user))
                    return;
                string remembered = user;
                await _sync.UpdateVmAsync(vm.Id, (_, m) => m.Credential ??= new StoredCredential { Username = remembered });
                await RefreshAsync();
            }

            try
            {
                var cmd = SshCommandBuilder.Build(vm.Summary, user, transport ?? SshCommandBuilder.DefaultTransport(vm.Summary));
                // UseShellExecute=false from a GUI process gives ssh a fresh
                // console, hosted by the user's default terminal app.
                var psi = new ProcessStartInfo(cmd.FileName) { UseShellExecute = false };
                foreach (var arg in cmd.Arguments)
                    psi.ArgumentList.Add(arg);
                Process.Start(psi);
                StatusMessage = $"Opened SSH to {vm.Name} as {user}.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                ErrorMessage = ex.Message;
            }
        }

        // ── delete ───────────────────────────────────────────────────────

        /// <summary>
        /// Plans, shows the plan, and executes exactly that plan: the service
        /// re-plans and refuses if anything changed in between.
        /// </summary>
        private async Task DeleteVmAsync()
        {
            var vm = SelectedVm;
            if (vm == null)
                return;
            StatusMessage = $"Checking what deleting '{vm.Name}' removes…";
            DeletionPlan plan;
            try
            {
                plan = await _deletion.PlanAsync(vm.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not plan deleting {Vm}", vm.Name);
                ErrorMessage = $"Could not check '{vm.Name}': {ex.Message}";
                StatusMessage = null;
                return;
            }
            StatusMessage = null;
            if (_dialogs.ReviewDeletion(plan) is not DeletionChoice choice)
                return;

            StatusMessage = $"Deleting '{vm.Name}'…";
            DeletionResult? result = null;
            await ForEachVm(new[] { vm }, "delete", AuditActions.VmDelete, async _ =>
            {
                result = await _deletion.ExecuteAsync(plan, choice.Permanently);
                string files = result.DeletedFiles.Count == 0 ? "no files"
                    : $"{result.DeletedFiles.Count} file(s), {DisplayFormat.Bytes(result.BytesFreed)} "
                      + (choice.Permanently ? "deleted permanently" : "to the Recycle Bin")
                      + ": " + string.Join(", ", result.DeletedFiles);
                return result.Complete ? files : $"{files}; not removed: {string.Join(", ", result.FailedFiles.Select(f => f.Path))}";
            });
            if (result is { Complete: false })
                ErrorMessage = $"'{vm.Name}' is deleted, but some files are still there:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, result.FailedFiles.Select(f => $"{f.Path}: {f.Error}"));
        }

        // ── archive ──────────────────────────────────────────────────────

        /// <summary>
        /// Exports every VM in the folder (and its subfolders) with a manifest,
        /// then offers to delete the exported VMs through the delete planner.
        /// </summary>
        private async Task ArchiveFolderAsync()
        {
            var node = SelectedNode;
            if (node?.Kind != NavNodeKind.Folder)
                return;
            string folderPath = FolderLabel(node.FolderId);
            var vms = Vms.Where(v => v.FolderId is Guid f && node.SelfAndDescendants().Any(n => n.FolderId == f)).ToList();
            if (vms.Count == 0)
            {
                ErrorMessage = $"'{folderPath}' holds no VMs to archive.";
                return;
            }
            var running = vms.Where(v => v.State is not (VmState.Off or VmState.Saved)).ToList();
            if (running.Count > 0)
            {
                ErrorMessage = $"Turn these off (or save them) first, so the archive is consistent: {string.Join(", ", running.Select(v => v.Name))}";
                return;
            }

            string? destination = _dialogs.PickFolder($"Archive '{folderPath}' to…");
            if (destination == null)
                return;

            long estimate = 0;
            try
            {
                var graph = await _host.ReadStorageGraphAsync();
                foreach (var vm in vms)
                {
                    estimate += graph.Attachments.Where(a => a.VmId == vm.Id && a.Kind == AttachedMediaKind.VirtualHardDisk)
                        .SelectMany(a => DeletionPlanner.Chain(a.Path, graph))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Sum(p => graph.Files.TryGetValue(p, out var f) ? f.SizeBytes ?? 0 : 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not estimate the archive size");
            }
            long? free = null;
            try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!).AvailableFreeSpace; }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }

            string size = estimate > 0 ? $" (about {DisplayFormat.Bytes(estimate)})" : "";
            string space = free is long f2 && estimate > f2
                ? $"\n\nWARNING: only {DisplayFormat.Bytes(f2)} is free on that drive."
                : "";
            if (!_dialogs.Confirm("Archive folder",
                    $"Export {vms.Count} VM(s){size} from '{folderPath}' to\n{destination}?\n\n" +
                    "Each VM is fully exported (configuration, checkpoints, disks; linked clones include their parent disk) " +
                    "and checksummed in manifest.json. Keep the VMs off until it finishes." + space))
                return;

            _archiveCts = new CancellationTokenSource();
            OnPropertyChanged(nameof(IsArchiving));
            ArchiveResult result;
            try
            {
                var items = await _store.LoadAsync();
                var progress = new Progress<string>(text =>
                {
                    if (_archiveCts?.IsCancellationRequested != true)
                        StatusMessage = text;
                });
                var token = _archiveCts.Token;
                token.Register(() => Application.Current?.Dispatcher.BeginInvoke(() =>
                    StatusMessage = "Cancelling the archive: stopping the export and removing the partial copy…"));
                result = await _archiver.ArchiveAsync(folderPath,
                    vms.Select(v => new ArchiveItem(v.Id, v.Name, items.FindVm(v.Id))).ToList(), destination, progress, token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Archiving {Folder} failed", folderPath);
                ErrorMessage = $"Could not archive '{folderPath}': {ex.Message}";
                StatusMessage = null;
                return;
            }
            finally
            {
                _archiveCts.Dispose();
                _archiveCts = null;
                OnPropertyChanged(nameof(IsArchiving));
                CommandManager.InvalidateRequerySuggested();
            }

            foreach (var entry in result.Manifest.Vms)
                await AuditAsync(AuditActions.Archive, entry.VmId, entry.Name,
                    entry.Error == null ? $"to {Path.Combine(result.Directory, entry.Directory)} ({DisplayFormat.Bytes(entry.Files.Sum(f => f.Size))})" : null,
                    entry.Error);
            StatusMessage = (result.Cancelled ? "Archive cancelled. Kept " : "Archived ")
                + $"{result.Exported.Count()} VM(s), {DisplayFormat.Bytes(result.TotalBytes)}, in {result.Directory}";
            var failed = result.Failed.Where(v => !result.Cancelled || v != result.Manifest.Vms[^1]).ToList();
            if (failed.Count > 0)
                ErrorMessage = "Not archived:" + Environment.NewLine
                    + string.Join(Environment.NewLine, failed.Select(v => $"{v.Name}: {v.Error}"));
            await LoadHistoryAsync();

            // A cancelled archive is incomplete; deleting part of the folder is not what the user asked for.
            if (!result.Cancelled)
                await OfferDeleteAfterArchiveAsync(node.Name, result);
        }

        private async Task OfferDeleteAfterArchiveAsync(string folderName, ArchiveResult result)
        {
            var exported = result.Exported.ToList();
            if (exported.Count == 0)
                return;

            var plans = new List<DeletionPlan>();
            var notes = new List<string>();
            foreach (var entry in exported)
            {
                try
                {
                    var plan = await _deletion.PlanAsync(entry.VmId);
                    if (plan.CanExecute)
                    {
                        plans.Add(plan);
                        notes.Add($"• {plan.VmName}: {plan.FilesToDelete.Count()} file(s), {DisplayFormat.Bytes(plan.BytesToDelete)}");
                    }
                    else
                    {
                        notes.Add($"• {plan.VmName}: stays. {string.Join(" ", plan.Blockers)}");
                    }
                }
                catch (Exception ex)
                {
                    notes.Add($"• {entry.Name}: stays. {ex.Message}");
                }
            }
            if (plans.Count == 0)
                return;

            long bytes = plans.Sum(p => p.BytesToDelete);
            if (!_dialogs.ConfirmByTyping("Delete archived VMs",
                    $"The archive is complete: {result.Directory}\n\n" +
                    $"Delete {plans.Count} archived VM(s) from this host now, freeing {DisplayFormat.Bytes(bytes)}? " +
                    "Files are deleted permanently (the archive is the copy); disks shared with other VMs stay.\n\n" +
                    string.Join("\n", notes) +
                    $"\n\nType the folder name ({folderName}) to delete, or cancel to keep them.", folderName))
                return;

            var byId = plans.ToDictionary(p => p.VmId);
            var targets = Vms.Where(v => byId.ContainsKey(v.Id)).ToList();
            await ForEachVm(targets, "delete", AuditActions.VmDelete, async vm =>
            {
                var deleted = await _deletion.ExecuteAsync(byId[vm.Id], permanently: true);
                string detail = $"after archiving to {result.Directory}: {deleted.DeletedFiles.Count} file(s), {DisplayFormat.Bytes(deleted.BytesFreed)} deleted permanently";
                return deleted.Complete ? detail : $"{detail}; not removed: {string.Join(", ", deleted.FailedFiles.Select(f => f.Path))}";
            });
        }

        // ── templates and linked clones ──────────────────────────────────

        private string? DescribeLineage(VmItemViewModel vm)
        {
            if (vm.IsTemplate)
            {
                int clones = Vms.Count(v => v.TemplateId == vm.Id);
                return $"Template for {clones} linked clone(s). Its disks are their parents: it cannot be started from the Manager.";
            }
            if (vm.TemplateId is Guid t)
                return _itemsById.TryGetValue(t, out var template) ? $"Linked clone of '{template.Name}'" : "Linked clone (template no longer exists!)";
            return null;
        }

        private async Task StartAsync()
        {
            var templates = SelectedVms.Where(v => v.IsTemplate).ToList();
            if (templates.Count > 0)
                ErrorMessage = $"Not started: {string.Join(", ", templates.Select(v => v.Name))} — a template must stay off; booting it would corrupt its linked clones. Unmark it first if it has none.";
            await ForEachVm(SelectedVms.Where(v => !v.IsTemplate && v.State is VmState.Off or VmState.Saved).ToList(), "start", AuditActions.VmStart, async vm =>
            {
                await _host.RequestStateChangeAsync(vm.Id, RequestedVmState.Running);
                return null;
            });
        }

        private async Task MarkTemplateAsync()
        {
            var vm = SelectedVm;
            if (vm == null)
                return;
            if (vm.CheckpointCount > 0)
            {
                ErrorMessage = $"'{vm.Name}' has checkpoints. Delete or apply them first: clones must build on the final disk, not on a checkpoint.";
                return;
            }
            if (!_dialogs.Confirm("Make template",
                    $"Make '{vm.Name}' a template for linked clones?\n\n" +
                    "• Clones get differencing disks on top of its disks, so it must never boot again: the Manager will refuse to start it.\n" +
                    "• It is delete-protected.\n" +
                    "• Prepare it first (generalize, clean machine-id / SSH host keys) and shut it down."))
                return;
            await ForEachVm(new[] { vm }, "make a template of", AuditActions.TemplateMark, async v =>
            {
                await _sync.UpdateVmAsync(v.Id, (_, meta) =>
                {
                    meta.IsTemplate = true;
                    meta.Protected = true;
                });
                return null;
            });
        }

        private async Task UnmarkTemplateAsync()
        {
            var vm = SelectedVm;
            if (vm == null)
                return;
            int clones = Vms.Count(v => v.TemplateId == vm.Id);
            if (clones > 0
                && !_dialogs.ConfirmByTyping("Unmark template",
                    $"{clones} linked clone(s) build on the disks of '{vm.Name}'. Booting or changing it afterwards corrupts them.\n\nType the VM name to unmark it anyway.", vm.Name))
                return;
            await ForEachVm(new[] { vm }, "unmark", AuditActions.TemplateUnmark, async v =>
            {
                await _sync.UpdateVmAsync(v.Id, (_, meta) => meta.IsTemplate = false);
                return clones > 0 ? $"{clones} clone(s) still depend on it" : null;
            });
        }

        private async Task NewLinkedCloneAsync()
        {
            var template = SelectedVm;
            if (template == null)
                return;
            string suggestion = Enumerable.Range(1, 999).Select(i => $"{template.Name}-clone{i}")
                .First(n => !Vms.Any(v => string.Equals(v.Name, n, StringComparison.OrdinalIgnoreCase)));
            string? name = _dialogs.Prompt("New linked clone", $"Name of the new VM (built on the disks of '{template.Name}'):", suggestion)?.Trim();
            if (string.IsNullOrEmpty(name))
                return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                ErrorMessage = $"'{name}' cannot be used as a folder name.";
                return;
            }

            StatusMessage = $"Creating linked clone '{name}'…";
            await ForEachVm(new[] { template }, $"clone '{template.Name}' as '{name}'", AuditActions.CloneCreate, async t =>
            {
                string folder = Path.Combine(await _host.GetDefaultVmFolderAsync(), name);
                string notes = NotesBlock.Parse(t.Summary.Notes).FreeText;
                var cloneId = await _host.CreateLinkedCloneAsync(t.Id, name, folder, notes);
                await _store.UpdateAsync(doc => doc.RecordLinkedClone(cloneId, doc.FindVm(t.Id), t.Id, DateTimeOffset.Now));
                await _sync.PushAllToNotesAsync(new[] { cloneId });
                await AuditAsync(AuditActions.CloneCreate, cloneId, name, $"from template '{t.Name}' in {folder}");
                return $"created '{name}'";
            });
        }

        // ── network ──────────────────────────────────────────────────────

        private void UpdateSwitchOptions()
        {
            var options = _inventory.Switches.Select(s => new SwitchOption(s.Id, s.Name, VmInventoryService.ExposureOf(s))).ToList();
            if (options.SequenceEqual(SwitchOptions))
                return;
            SwitchOptions.Clear();
            foreach (var o in options)
                SwitchOptions.Add(o);
        }

        /// <summary>Panic button: disconnects every adapter of the selected VMs, remembering which, so Restore can undo it.</summary>
        private Task IsolateNetworkAsync()
            => ForEachVm(SelectedVms.Where(v => v.Exposure != NetworkExposure.None).ToList(), "isolate", AuditActions.NetworkIsolate, async vm =>
            {
                string from = ConnectedSwitches(vm);
                var ports = await _host.DisconnectNetworkAsync(vm.Id);
                await _store.UpdateAsync(doc =>
                {
                    var meta = doc.GetOrAddVm(vm.Id);
                    meta.IsolatedPorts = (meta.IsolatedPorts ?? new List<string>()).Union(ports, StringComparer.OrdinalIgnoreCase).ToList();
                    return true;
                });
                return $"disconnected from {from}";
            });

        private Task RestoreNetworkAsync()
            => ForEachVm(SelectedVms.Where(v => v.CanReconnectNetwork).ToList(), "restore the network of", AuditActions.NetworkRestore, async vm =>
            {
                // Without a record of what Isolate disconnected, reconnect everything that remembers a switch.
                var isolated = vm.Metadata?.IsolatedPorts is { Count: > 0 } ports ? ports : null;
                int count = await _host.ReconnectNetworkAsync(vm.Id, isolated);
                await ClearIsolationAsync(vm.Id);
                return $"{count} adapter(s) reconnected";
            });

        private async Task ConnectToSwitchAsync(SwitchOption target)
        {
            var targets = SelectedVms.ToList();
            if (target.Exposure == NetworkExposure.External
                && !_dialogs.Confirm("Connect to external switch",
                    $"'{target.Name}' bridges onto the physical network: the VM becomes reachable from your LAN.\n\nConnect {string.Join(", ", targets.Select(v => v.Name))}?"))
                return;
            await ForEachVm(targets, $"connect to '{target.Name}'", AuditActions.NetworkConnect, async vm =>
            {
                string from = ConnectedSwitches(vm);
                await _host.ConnectNetworkToSwitchAsync(vm.Id, target.Id);
                await ClearIsolationAsync(vm.Id);
                return $"{from} → {target.Name}";
            });
        }

        private Task ClearIsolationAsync(Guid vmId)
            => _store.UpdateAsync(doc =>
            {
                if (doc.FindVm(vmId) is { IsolatedPorts: not null } meta)
                    meta.IsolatedPorts = null;
                return true;
            });

        private static string ConnectedSwitches(VmItemViewModel vm)
        {
            var names = vm.Summary.Switches.Where(s => s != "Not connected").Distinct().ToList();
            return names.Count == 0 ? "(nothing)" : string.Join(", ", names);
        }

        /// <summary>
        /// Runs a per-VM action one VM at a time, audits each outcome (the action
        /// returns the audit detail), then reports failures together and refreshes.
        /// </summary>
        private async Task ForEachVm(IReadOnlyList<VmItemViewModel> targets, string verb, string auditAction, Func<VmItemViewModel, Task<string?>> action)
        {
            var errors = new List<string>();
            foreach (var vm in targets)
            {
                string? detail = null;
                string? error = null;
                try
                {
                    detail = await action(vm);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to {Verb} {Vm}", verb, vm.Name);
                    error = ex.Message;
                    errors.Add($"{vm.Name}: {ex.Message}");
                }
                await AuditAsync(auditAction, vm.Id, vm.Name, detail, error);
            }
            ErrorMessage = errors.Count == 0 ? null : $"Could not {verb}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}";
            if (errors.Count < targets.Count)
                StatusMessage = $"Done: {verb} {targets.Count - errors.Count} VM(s).";
            await RefreshAsync();
        }

        // ── folders ──────────────────────────────────────────────────────

        private async Task NewFolderAsync()
        {
            Guid? parent = SelectedNode?.Kind == NavNodeKind.Folder ? SelectedNode.FolderId : null;
            string? name = _dialogs.Prompt("New folder", parent == null ? "Folder name:" : $"Subfolder of '{SelectedNode!.Name}':", "");
            if (string.IsNullOrWhiteSpace(name))
                return;
            string path = parent == null ? name.Trim() : $"{FolderLabel(parent)}/{name.Trim()}";
            string? error = await RunMetadataChange(doc => doc.CreateFolder(name, parent), affectsNotes: false);
            await AuditAsync(AuditActions.FolderCreate, null, null, path, error);
        }

        private async Task RenameFolderAsync()
        {
            var node = SelectedNode;
            if (node?.FolderId is not Guid id)
                return;
            string? name = _dialogs.Prompt("Rename folder", "New name:", node.Name);
            if (string.IsNullOrWhiteSpace(name) || name == node.Name)
                return;
            string oldPath = FolderLabel(id);
            string? error = await RunMetadataChange(doc => doc.RenameFolder(id, name), affectsNotes: true);
            await AuditAsync(AuditActions.FolderRename, null, null, $"{oldPath} → {name.Trim()}", error);
        }

        private async Task DeleteFolderAsync()
        {
            var node = SelectedNode;
            if (node?.FolderId is not Guid id)
                return;
            if (!_dialogs.Confirm("Delete folder", $"Delete folder '{node.Name}'?\n\nVMs and subfolders inside it move up one level; no VM is deleted."))
                return;
            string path = FolderLabel(id);
            string? error = await RunMetadataChange(doc => doc.DeleteFolder(id), affectsNotes: true);
            await AuditAsync(AuditActions.FolderDelete, null, null, path, error);
        }

        /// <summary>Engagement end date for a folder; VMs inside without their own date inherit it.</summary>
        private async Task SetFolderExpiryAsync()
        {
            var node = SelectedNode;
            if (node?.FolderId is not Guid id)
                return;
            string? text = _dialogs.Prompt("Folder expiry",
                $"Engagement end date for '{node.Name}' (yyyy-MM-dd). VMs inside without their own date inherit it; nothing is deleted automatically.\n\nLeave empty to clear.",
                node.ExpiresOn?.ToString("yyyy-MM-dd") ?? DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"));
            if (text == null)
                return;
            DateOnly? date = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", out var parsed))
                {
                    ErrorMessage = $"'{text}' is not a date in yyyy-MM-dd form.";
                    return;
                }
                date = parsed;
            }
            if (date == node.ExpiresOn)
                return;
            string path = FolderLabel(id);
            string? error = await RunMetadataChange(doc => doc.SetFolderExpiry(id, date), affectsNotes: false);
            await AuditAsync(AuditActions.FolderExpiry, null, null, $"{path}: {date?.ToString("yyyy-MM-dd") ?? "cleared"}", error);
        }

        private void ShowExpired()
        {
            var node = Navigation.FirstOrDefault(n => n.Kind == NavNodeKind.Smart && n.Filter?.ExpiredOnly == true);
            if (node != null)
            {
                node.IsSelected = true;
                SelectedNode = node;
            }
        }

        /// <summary>Drop handler: moves VMs into a folder (null = out of any folder).</summary>
        public async Task MoveVmsToFolderAsync(IReadOnlyCollection<Guid> vmIds, Guid? folderId)
        {
            var moved = vmIds.Where(_itemsById.ContainsKey).Select(id => _itemsById[id])
                .Where(vm => vm.FolderId != folderId)
                .Select(vm => (vm.Id, vm.Name, From: FolderLabel(vm.FolderId))).ToList();
            string to = FolderLabel(folderId);
            string? error = await RunMetadataChange(doc =>
            {
                foreach (var id in vmIds)
                    doc.MoveVmToFolder(id, folderId);
            }, affectsNotes: true, onlyVms: vmIds);
            foreach (var (id, name, from) in moved)
                await AuditAsync(AuditActions.VmMove, id, name, $"{from} → {to}", error);
        }

        /// <summary>Folder context menu: sets or (with null) clears the icon colour.</summary>
        public Task SetFolderColorAsync(Guid folderId, string? color)
            => RunMetadataChange(doc => doc.SetFolderColor(folderId, color), affectsNotes: false);

        /// <summary>Drop handler: re-parents a folder.</summary>
        public async Task MoveFolderAsync(Guid folderId, Guid? newParentId)
        {
            string from = FolderLabel(folderId);
            string? error = await RunMetadataChange(doc => doc.MoveFolder(folderId, newParentId), affectsNotes: true);
            await AuditAsync(AuditActions.FolderMove, null, null, $"{from} → {_doc.GetFolderPath(newParentId) ?? "(top level)"}", error);
        }

        /// <summary>
        /// Applies a store change, then re-mirrors the affected VMs' Notes:
        /// folder paths are part of the Notes block, so renaming or moving a
        /// folder changes the block of every VM below it. Returns the error shown, or null.
        /// </summary>
        private async Task<string?> RunMetadataChange(Action<MetadataDocument> change, bool affectsNotes, IReadOnlyCollection<Guid>? onlyVms = null)
        {
            try
            {
                await _store.UpdateAsync(doc => { change(doc); return true; });
                if (affectsNotes)
                {
                    var doc = await _store.LoadAsync();
                    // PushToNotes skips unchanged VMs, so syncing every known VM is cheap.
                    var ids = onlyVms ?? doc.Vms.Select(v => v.VmId).Where(_itemsById.ContainsKey).ToList();
                    await _sync.PushAllToNotesAsync(ids);
                }
                await RefreshAsync();
                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or MetadataStoreException or HyperVOperationException)
            {
                return ErrorMessage = ex.Message;
            }
        }
    }
}
