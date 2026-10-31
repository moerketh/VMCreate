using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VMCreate.Manager.ViewModels;

namespace VMCreate.Manager
{
    public partial class MainWindow
    {
        private const string VmDragFormat = "VMCreate.Manager.VmIds";
        private const string FolderDragFormat = "VMCreate.Manager.FolderId";

        private readonly MainViewModel _vm;
        private Point? _gridDragStart;
        private Point? _treeDragStart;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _vm = viewModel;
            DataContext = viewModel;
            Closed += (_, _) => _vm.Dispose();
        }

        // ── selection ────────────────────────────────────────────────────

        private void NavTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
            => _vm.SelectedNode = e.NewValue as NavNodeViewModel;

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
