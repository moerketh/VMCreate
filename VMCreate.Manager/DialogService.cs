using System.Windows;
using VMCreate.Manager.Deletion;

namespace VMCreate.Manager
{
    public interface IDialogService
    {
        /// <summary>Asks for a line of text; null when cancelled.</summary>
        string? Prompt(string title, string message, string initialValue);

        bool Confirm(string title, string message);

        /// <summary>Confirmation that only succeeds when the user types <paramref name="expected"/> exactly.</summary>
        bool ConfirmByTyping(string title, string message, string expected);

        /// <summary>Shows what deleting a VM removes; null when cancelled (or the plan is blocked).</summary>
        DeletionChoice? ReviewDeletion(DeletionPlan plan);

        /// <summary>A folder picker; null when cancelled.</summary>
        string? PickFolder(string title);
    }

    public sealed record DeletionChoice(bool Permanently);

    public sealed class DialogService : IDialogService
    {
        public string? Prompt(string title, string message, string initialValue)
        {
            var dialog = new InputDialog(title, message, initialValue) { Owner = Application.Current.MainWindow };
            return dialog.ShowDialog() == true ? dialog.Value.Trim() : null;
        }

        public bool Confirm(string title, string message)
            => System.Windows.MessageBox.Show(Application.Current.MainWindow, message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

        public bool ConfirmByTyping(string title, string message, string expected)
            => Prompt(title, message, "") == expected;

        public DeletionChoice? ReviewDeletion(DeletionPlan plan)
        {
            var dialog = new DeletePreviewDialog(plan) { Owner = Application.Current.MainWindow };
            return dialog.ShowDialog() == true && plan.CanExecute ? new DeletionChoice(dialog.Permanently) : null;
        }

        public string? PickFolder(string title)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
        }
    }
}
