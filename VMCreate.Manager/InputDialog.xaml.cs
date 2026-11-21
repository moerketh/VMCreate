using System.Windows;
using System.Windows.Input;

namespace VMCreate.Manager
{
    public partial class InputDialog
    {
        public InputDialog(string title, string message, string initialValue)
        {
            InitializeComponent();
            Title = title;
            DialogTitleBar.Title = title;
            MessageText.Text = message;
            ValueBox.Text = initialValue;
            Loaded += (_, _) =>
            {
                ValueBox.Focus();
                ValueBox.SelectAll();
            };
        }

        public string Value => ValueBox.Text;

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void ValueBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                DialogResult = true;
        }
    }
}
