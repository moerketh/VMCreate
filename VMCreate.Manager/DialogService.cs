using System.Windows;

namespace VMCreate.Manager
{
    public interface IDialogService
    {
        /// <summary>Asks for a line of text; null when cancelled.</summary>
        string? Prompt(string title, string message, string initialValue);

        bool Confirm(string title, string message);

        /// <summary>Confirmation that only succeeds when the user types <paramref name="expected"/> exactly.</summary>
        bool ConfirmByTyping(string title, string message, string expected);
    }

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
    }
}
