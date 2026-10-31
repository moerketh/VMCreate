using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
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
        private readonly IDialogService _dialogs;
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
        private bool _isLoading = true;

        public MainViewModel(
            IVmInventoryService inventory,
            IHyperVHost host,
            JsonVmMetadataStore store,
            VmMetadataSync sync,
            IDialogService dialogs,
            ILogger<MainViewModel> logger)
        {
            _inventory = inventory;
            _host = host;
            _store = store;
            _sync = sync;
            _dialogs = dialogs;
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
            StartCommand = new AsyncRelayCommand(() => ForSelected("start", id => _host.RequestStateChangeAsync(id, RequestedVmState.Running)),
                () => SelectedVms.Any(v => v.State is VmState.Off or VmState.Saved));
            ShutdownCommand = new AsyncRelayCommand(() => ForSelected("shut down", id => _host.ShutdownAsync(id, force: false)),
                () => SelectedVms.Any(v => v.State == VmState.Running));
            TurnOffCommand = new AsyncRelayCommand(TurnOffAsync,
                () => SelectedVms.Any(v => v.State is VmState.Running or VmState.Paused or VmState.Saved));
            SaveStateCommand = new AsyncRelayCommand(() => ForSelected("save", id => _host.RequestStateChangeAsync(id, RequestedVmState.Saved)),
                () => SelectedVms.Any(v => v.State is VmState.Running or VmState.Paused));
            PauseCommand = new AsyncRelayCommand(() => ForSelected("pause", id => _host.RequestStateChangeAsync(id, RequestedVmState.Paused)),
                () => SelectedVms.Any(v => v.State == VmState.Running));
            ResumeCommand = new AsyncRelayCommand(() => ForSelected("resume", id => _host.RequestStateChangeAsync(id, RequestedVmState.Running)),
                () => SelectedVms.Any(v => v.State == VmState.Paused));
            ConnectCommand = new RelayCommand(Connect, () => SelectedVm != null);
            CopyIpCommand = new RelayCommand(() => Clipboard.SetText(SelectedVm!.PrimaryIp!), () => SelectedVm?.PrimaryIp != null);
            SshCommand = new AsyncRelayCommand(() => OpenSshAsync(null), () => SelectedVm?.Summary.IsRunning == true);
            SshNetworkCommand = new AsyncRelayCommand(() => OpenSshAsync(SshTransport.Network), () => SelectedVm?.PrimaryIp != null);
            SshVsockCommand = new AsyncRelayCommand(() => OpenSshAsync(SshTransport.Vsock), () => SelectedVm?.Summary.IsRunning == true);
            MoveSelectedToFolderCommand = new RelayCommand<FolderOption>(
                f => _ = MoveVmsToFolderAsync(SelectedVms.Select(v => v.Id).ToList(), f?.Id),
                f => f != null && SelectedVms.Count > 0);

            NewFolderCommand = new AsyncRelayCommand(NewFolderAsync);
            RenameFolderCommand = new AsyncRelayCommand(RenameFolderAsync, () => SelectedNode?.Kind == NavNodeKind.Folder);
            DeleteFolderCommand = new AsyncRelayCommand(DeleteFolderAsync, () => SelectedNode?.Kind == NavNodeKind.Folder);

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

        /// <summary>Set by the view from DataGrid.SelectedItems (not bindable in WPF).</summary>
        public IReadOnlyList<VmItemViewModel> SelectedVms { get; private set; } = Array.Empty<VmItemViewModel>();

        public NavNodeViewModel? SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (SetProperty(ref _selectedNode, value))
                    VmsView.Refresh();
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
        public ICommand NewFolderCommand { get; }
        public ICommand RenameFolderCommand { get; }
        public ICommand DeleteFolderCommand { get; }
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
                    if (await _sync.AdoptFromNotesAsync(vms) is int adopted and > 0)
                        StatusMessage = $"Restored metadata of {adopted} VM(s) from their Notes.";
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
                string? path = _doc.GetFolderPath(meta?.FolderId);
                if (_itemsById.TryGetValue(summary.Id, out var item))
                {
                    item.Update(summary, meta, path);
                }
                else
                {
                    item = new VmItemViewModel(summary, meta, path);
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
            UpdateCounts();
            LegacyCredentialCount = vms.Count(VmMetadataSync.HasLegacyCredentials);
            VmsView.Refresh();

            if (SelectedVm != null && !_itemsById.ContainsKey(SelectedVm.Id))
                SetSelection(Array.Empty<VmItemViewModel>());
            else
                LoadEditor(force: false);
        }

        // ── navigation tree ──────────────────────────────────────────────

        private void RebuildNavigationIfNeeded()
        {
            string signature = string.Join("|", _doc.Folders.OrderBy(f => f.Id).Select(f => $"{f.Id}:{f.ParentId}:{f.Name}"))
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
                var node = new NavNodeViewModel(NavNodeKind.Folder, folder.Name, "Folder24", folder.Id);
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
            NavNodeKind.Smart => node.Filter!.Matches(vm.Name, vm.State.ToString(), vm.Metadata, DateOnly.FromDateTime(DateTime.Today)),
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
            Editor = new VmEditorViewModel(SelectedVm);
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

            try
            {
                string newName = editor.Name.Trim();
                if (newName.Length > 0 && newName != editor.Item.Name)
                    await _host.RenameAsync(editor.VmId, newName);

                await _sync.UpdateVmAsync(editor.VmId, (_, vm) => editor.ApplyTo(vm));
                editor.MarkClean();
                StatusMessage = $"Saved '{newName}'.";
                await RefreshAsync();
                LoadEditor(force: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving VM {VmId} failed", editor.VmId);
                ErrorMessage = ex.Message;
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
            await ForSelected("turn off", id => _host.RequestStateChangeAsync(id, RequestedVmState.Off));
        }

        private async Task ForSelected(string verb, Func<Guid, Task> action)
        {
            var targets = SelectedVms.ToList();
            var errors = new List<string>();
            await Task.WhenAll(targets.Select(async vm =>
            {
                try
                {
                    await action(vm.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to {Verb} {Vm}", verb, vm.Name);
                    lock (errors)
                        errors.Add($"{vm.Name}: {ex.Message}");
                }
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

        // ── folders ──────────────────────────────────────────────────────

        private async Task NewFolderAsync()
        {
            Guid? parent = SelectedNode?.Kind == NavNodeKind.Folder ? SelectedNode.FolderId : null;
            string? name = _dialogs.Prompt("New folder", parent == null ? "Folder name:" : $"Subfolder of '{SelectedNode!.Name}':", "");
            if (string.IsNullOrWhiteSpace(name))
                return;
            await RunMetadataChange(doc => doc.CreateFolder(name, parent), affectsNotes: false);
        }

        private async Task RenameFolderAsync()
        {
            var node = SelectedNode;
            if (node?.FolderId is not Guid id)
                return;
            string? name = _dialogs.Prompt("Rename folder", "New name:", node.Name);
            if (string.IsNullOrWhiteSpace(name) || name == node.Name)
                return;
            await RunMetadataChange(doc => doc.RenameFolder(id, name), affectsNotes: true);
        }

        private async Task DeleteFolderAsync()
        {
            var node = SelectedNode;
            if (node?.FolderId is not Guid id)
                return;
            if (!_dialogs.Confirm("Delete folder", $"Delete folder '{node.Name}'?\n\nVMs and subfolders inside it move up one level; no VM is deleted."))
                return;
            await RunMetadataChange(doc => doc.DeleteFolder(id), affectsNotes: true);
        }

        /// <summary>Drop handler: moves VMs into a folder (null = out of any folder).</summary>
        public Task MoveVmsToFolderAsync(IReadOnlyCollection<Guid> vmIds, Guid? folderId)
            => RunMetadataChange(doc =>
            {
                foreach (var id in vmIds)
                    doc.MoveVmToFolder(id, folderId);
            }, affectsNotes: true, onlyVms: vmIds);

        /// <summary>Drop handler: re-parents a folder.</summary>
        public Task MoveFolderAsync(Guid folderId, Guid? newParentId)
            => RunMetadataChange(doc => doc.MoveFolder(folderId, newParentId), affectsNotes: true);

        /// <summary>
        /// Applies a store change, then re-mirrors the affected VMs' Notes:
        /// folder paths are part of the Notes block, so renaming or moving a
        /// folder changes the block of every VM below it.
        /// </summary>
        private async Task RunMetadataChange(Action<MetadataDocument> change, bool affectsNotes, IReadOnlyCollection<Guid>? onlyVms = null)
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
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or MetadataStoreException or HyperVOperationException)
            {
                ErrorMessage = ex.Message;
            }
        }
    }
}
