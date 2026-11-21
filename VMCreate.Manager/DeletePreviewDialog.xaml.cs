using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VMCreate.Manager.Deletion;
using VMCreate.Manager.Inventory;

namespace VMCreate.Manager
{
    /// <summary>
    /// Shows a <see cref="DeletionPlan"/> file by file. Deleting needs the VM's
    /// name typed in; a blocked plan can only be closed.
    /// </summary>
    public partial class DeletePreviewDialog
    {
        private static readonly Brush DeleteBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B)));
        private static readonly Brush KeepBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x57, 0xAB, 0x5A)));
        private static readonly Brush MissingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)));

        private readonly DeletionPlan _plan;

        public sealed record FileRow(string ActionText, Brush ActionBrush, string KindText, string SizeText, string Path, string Reason);

        public DeletePreviewDialog(DeletionPlan plan)
        {
            InitializeComponent();
            _plan = plan;
            Title = $"Delete {plan.VmName}";
            DialogTitleBar.Title = Title;

            FilesGrid.ItemsSource = plan.Files.Select(ToRow).ToList();

            int count = plan.FilesToDelete.Count();
            int kept = plan.Files.Count(f => f.Action == PlannedAction.Keep);
            SummaryText.Text = $"Removes '{plan.VmName}' from Hyper-V (configuration, checkpoints, saved state) and deletes "
                + (count == 0 ? "no files." : $"{count} file(s), {DisplayFormat.Bytes(plan.BytesToDelete)}.")
                + (kept > 0 ? $" {kept} file(s) stay because something else uses them or they are not this VM's own." : "");
            FoldersText.Text = plan.Folders.Count == 0
                ? "No folders are removed: the VM's files live in shared folders."
                : "Removed afterwards, if empty: " + string.Join("; ", plan.Folders);

            if (!plan.CanExecute)
            {
                BlockerPanel.Visibility = Visibility.Visible;
                BlockerText.Text = string.Join(Environment.NewLine, plan.Blockers);
                ConfirmPanel.Visibility = Visibility.Collapsed;
                DeleteButton.Visibility = Visibility.Collapsed;
                CancelButton.Content = "Close";
            }
            else
            {
                TypeNamePrompt.Text = $"Type the VM name ({plan.VmName}) to confirm:";
                Loaded += (_, _) => NameBox.Focus();
            }
        }

        public bool Permanently => PermanentBox.IsChecked == true;

        private static FileRow ToRow(PlannedFile f) => new(
            f.Action switch { PlannedAction.Delete => "Delete", PlannedAction.Keep => "Keep", _ => "Missing" },
            f.Action switch { PlannedAction.Delete => DeleteBrush, PlannedAction.Keep => KeepBrush, _ => MissingBrush },
            f.Kind switch
            {
                PlannedFileKind.CheckpointDisk => "Checkpoint disk",
                PlannedFileKind.Iso => "ISO",
                PlannedFileKind.Floppy => "Floppy",
                _ => "Virtual disk",
            },
            f.SizeBytes is long size && f.Action != PlannedAction.Missing ? DisplayFormat.Bytes(size) : "",
            f.Path,
            f.Reason);

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
            => DeleteButton.IsEnabled = _plan.CanExecute && NameBox.Text == _plan.VmName;

        private void Permanent_Changed(object sender, RoutedEventArgs e)
            => RecycleNote.Text = Permanently
                ? "Files are deleted for good. There is no undo."
                : "Files go to the Recycle Bin. Windows asks before destroying a file that is too big for it.";

        private void Delete_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
