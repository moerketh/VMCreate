using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VMCreate.Manager.ViewModels;
using VMCreate.Metadata;

namespace VMCreate.Manager
{
    public partial class MainWindow
    {
        private const string VmDragFormat = "VMCreate.Manager.VmIds";
        private const string FolderDragFormat = "VMCreate.Manager.FolderId";

        private readonly MainViewModel _vm;
        private readonly IAuditLog _audit;
        private readonly Func<StorageWindow> _storageWindow;
        private Point? _gridDragStart;
        private Point? _treeDragStart;
        // Group containers are recreated on every view refresh, so expansion state lives here.
        private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);

        public MainWindow(MainViewModel viewModel, IAuditLog audit, Func<StorageWindow> storageWindow)
        {
            InitializeComponent();
            _vm = viewModel;
            _audit = audit;
            _storageWindow = storageWindow;
            DataContext = viewModel;
            VmGrid.PreviewMouseRightButtonDown += VmGrid_PreviewMouseRightButtonDown;
            BuildColumnChooser();
            RestoreLayout(UiSettings.Load());
            Closing += (_, _) => SaveLayout();
            Closed += (_, _) => _vm.Dispose();
            // Switching grouping on rebuilds the rows presenter; WPF redistributes column widths while the
            // grid's scroll host is momentarily absent (0 px available), squeezing every column to its
            // minimum, and never gives the space back. Re-assert the intended widths once layout settles.
            ((INotifyCollectionChanged)_vm.VmsView.GroupDescriptions).CollectionChanged += (_, _) =>
                Dispatcher.BeginInvoke(ReapplyColumnWidths, DispatcherPriority.ContextIdle);
        }

        /// <summary>Brings the window to the front; used when a second instance is started.</summary>
        public void BringToFront()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Show();
            Activate();
            // Activate alone may only flash the taskbar button; a Topmost toggle reliably raises it.
            Topmost = true;
            Topmost = false;
            Focus();
        }

        // ── layout persistence ───────────────────────────────────────────

        private void RestoreLayout(UiSettings s)
        {
            if (s.Width is double w && s.Height is double h && s.Left is double l && s.Top is double t)
            {
                // Only restore a position that is still on a connected screen.
                var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (screen.IntersectsWith(new Rect(l, t, w, h)) && w >= MinWidth && h >= MinHeight)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = l;
                    Top = t;
                    Width = w;
                    Height = h;
                }
            }
            if (s.Maximized)
                WindowState = WindowState.Maximized;
            _collapsedGroups.UnionWith(s.CollapsedGroups);
            if (s.NavigationWidth is double nav && nav >= NavColumn.MinWidth)
                NavColumn.Width = new GridLength(nav);
            if (s.DetailsWidth is double det && det >= DetailsColumn.MinWidth)
                DetailsColumn.Width = new GridLength(det);

            foreach (var cs in s.Columns)
            {
                var col = VmGrid.Columns.FirstOrDefault(c => HeaderOf(c) == cs.Header);
                if (col == null)
                    continue;
                // A star/fixed mismatch means the column definition changed since the save.
                if (cs.Width > 0 && cs.IsStar == col.Width.IsStar)
                    col.Width = cs.IsStar ? new DataGridLength(cs.Width, DataGridLengthUnitType.Star) : new DataGridLength(cs.Width);
                col.Visibility = cs.Visible || IsNameColumn(col) ? Visibility.Visible : Visibility.Collapsed;
            }
            // Apply display order lowest first so indexes don't fight each other.
            foreach (var cs in s.Columns.OrderBy(c => c.DisplayIndex))
            {
                var col = VmGrid.Columns.FirstOrDefault(c => HeaderOf(c) == cs.Header);
                if (col != null && cs.DisplayIndex >= 0 && cs.DisplayIndex < VmGrid.Columns.Count)
                    col.DisplayIndex = cs.DisplayIndex;
            }

            var sortCol = VmGrid.Columns.FirstOrDefault(c => HeaderOf(c) == s.SortColumn);
            if (sortCol != null)
            {
                var direction = s.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                sortCol.SortDirection = direction;
                _vm.VmsView.SortDescriptions.Clear();
                _vm.VmsView.SortDescriptions.Add(new SortDescription(sortCol.SortMemberPath, direction));
            }
        }

        private void SaveLayout()
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            var sorted = VmGrid.Columns.FirstOrDefault(c => c.SortDirection != null);
            new UiSettings
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == WindowState.Maximized,
                NavigationWidth = NavColumn.ActualWidth,
                DetailsWidth = DetailsColumn.ActualWidth,
                SortColumn = sorted == null ? null : HeaderOf(sorted),
                SortDescending = sorted?.SortDirection == ListSortDirection.Descending,
                CollapsedGroups = _collapsedGroups.Order().ToList(),
                Columns = VmGrid.Columns.Select(c => new UiSettings.ColumnSetting
                {
                    Header = HeaderOf(c),
                    // The set width, not ActualWidth: a temporary squeeze (see ReapplyColumnWidths) must not be persisted.
                    Width = c.Width.IsStar || c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth,
                    IsStar = c.Width.IsStar,
                    DisplayIndex = c.DisplayIndex,
                    Visible = c.Visibility == Visibility.Visible,
                }).ToList(),
            }.Save();
        }

        /// <summary>Right-click on a column header: tick columns on or off. Name always stays.</summary>
        private void BuildColumnChooser()
        {
            var menu = new ContextMenu();
            foreach (var col in VmGrid.Columns.Where(c => !IsNameColumn(c)))
            {
                var item = new MenuItem { Header = HeaderOf(col), IsCheckable = true, StaysOpenOnClick = true };
                item.IsChecked = col.Visibility == Visibility.Visible;
                item.Checked += (_, _) => col.Visibility = Visibility.Visible;
                item.Unchecked += (_, _) => col.Visibility = Visibility.Collapsed;
                menu.Items.Add(item);
            }
            menu.Opened += (_, _) =>
            {
                foreach (MenuItem item in menu.Items)
                    item.IsChecked = VmGrid.Columns.First(c => HeaderOf(c) == (string)item.Header).Visibility == Visibility.Visible;
            };
            // WPF-UI keys its header style instead of making it implicit; basing on the type
            // alone falls back to the stock (white) Aero header.
            var baseStyle = (TryFindResource("DefaultDataGridColumnHeaderStyle")
                ?? TryFindResource(typeof(DataGridColumnHeader))) as Style;
            var style = new Style(typeof(DataGridColumnHeader), baseStyle);
            style.Setters.Add(new Setter(ContextMenuProperty, menu));
            VmGrid.ColumnHeaderStyle = style;
        }

        private static string HeaderOf(DataGridColumn column) => column.Header as string ?? "";

        private void ReapplyColumnWidths()
        {
            foreach (var col in VmGrid.Columns)
            {
                var width = col.Width;
                // An equal DataGridLength is not a property change, so bounce through Auto to force a recompute.
                col.Width = DataGridLength.Auto;
                col.Width = new DataGridLength(width.Value, width.UnitType);
            }
        }

        private void OpenActivityLog_Click(object sender, RoutedEventArgs e)
            => new ActivityLogWindow(_audit) { Owner = this }.Show();

        private void OpenStorage_Click(object sender, RoutedEventArgs e)
        {
            var window = _storageWindow();
            window.Owner = this;
            // Deleting files changes what the VM list shows (disk sizes, missing disks).
            window.Closed += (_, _) => _vm.RefreshCommand.Execute(null);
            window.Show();
        }

        // ── folder groups ────────────────────────────────────────────────

        private void GroupToggle_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton { DataContext: CollectionViewGroup { Name: string key } } toggle)
                toggle.IsChecked = !_collapsedGroups.Contains(key);
        }

        private void GroupToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton { IsLoaded: true, DataContext: CollectionViewGroup { Name: string key } } toggle)
                return;
            if (toggle.IsChecked == true)
                _collapsedGroups.Remove(key);
            else
                _collapsedGroups.Add(key);
        }

        private static bool IsNameColumn(DataGridColumn column) => HeaderOf(column) == "Name";

        /// <summary>Right-click on an unselected row selects just that row, so the menu acts on what was clicked.</summary>
        private void VmGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { IsSelected: false } row)
            {
                VmGrid.SelectedItems.Clear();
                row.IsSelected = true;
            }
        }

        // ── selection ────────────────────────────────────────────────────

        private void NavTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
            => _vm.SelectedNode = e.NewValue as NavNodeViewModel;

        /// <summary>Right-click selects the node first so the folder menu acts on it.</summary>
        private void NavTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
                item.IsSelected = true;
        }

        private async void FolderColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: FolderColorOption option } && _vm.SelectedNode?.FolderId is Guid id)
                await _vm.SetFolderColorAsync(id, option.Hex);
        }

        private void VmGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => _vm.SetSelection(VmGrid.SelectedItems.OfType<VmItemViewModel>().ToList());

        private void VmGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) != null && _vm.ConnectCommand.CanExecute(null))
                _vm.ConnectCommand.Execute(null);
        }

        // ── drag VMs from the grid ───────────────────────────────────────

        private void VmGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => _gridDragStart = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) != null ? e.GetPosition(VmGrid) : null;

        private void VmGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_gridDragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || !MovedEnough(start, e.GetPosition(VmGrid)))
                return;
            _gridDragStart = null;
            var ids = VmGrid.SelectedItems.OfType<VmItemViewModel>().Select(v => v.Id).ToArray();
            if (ids.Length > 0)
                DragDrop.DoDragDrop(VmGrid, new DataObject(VmDragFormat, ids), DragDropEffects.Move);
        }

        // ── drag folders inside the tree ─────────────────────────────────

        private void NavTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => _treeDragStart = NodeAt(e.OriginalSource)?.Kind == NavNodeKind.Folder ? e.GetPosition(NavTree) : null;

        private void NavTree_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_treeDragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || !MovedEnough(start, e.GetPosition(NavTree)))
                return;
            _treeDragStart = null;
            if (NodeAt(e.OriginalSource) is { Kind: NavNodeKind.Folder, FolderId: Guid id })
                DragDrop.DoDragDrop(NavTree, new DataObject(FolderDragFormat, id), DragDropEffects.Move);
        }

        // ── drop onto the tree ───────────────────────────────────────────

        private void NavTree_DragOver(object sender, DragEventArgs e)
        {
            var node = NodeAt(e.OriginalSource);
            bool ok = node != null && (
                (e.Data.GetDataPresent(VmDragFormat) && node.IsFolderDropTarget) ||
                (e.Data.GetDataPresent(FolderDragFormat) && node.Kind is NavNodeKind.All or NavNodeKind.Folder
                    && node.FolderId != (Guid?)e.Data.GetData(FolderDragFormat)));
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private async void NavTree_Drop(object sender, DragEventArgs e)
        {
            var node = NodeAt(e.OriginalSource);
            if (node == null)
                return;
            // "All" and "Not in a folder" both mean: no folder.
            Guid? target = node.Kind == NavNodeKind.Folder ? node.FolderId : null;

            if (e.Data.GetData(VmDragFormat) is Guid[] ids && node.IsFolderDropTarget)
                await _vm.MoveVmsToFolderAsync(ids, target);
            else if (e.Data.GetData(FolderDragFormat) is Guid folderId && node.Kind is NavNodeKind.All or NavNodeKind.Folder)
                await _vm.MoveFolderAsync(folderId, target);
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static bool MovedEnough(Point start, Point now)
            => Math.Abs(now.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(now.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

        private static NavNodeViewModel? NodeAt(object source)
            => FindAncestor<TreeViewItem>(source as DependencyObject)?.DataContext as NavNodeViewModel;

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T)
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }
    }
}
