using System.Windows;
using Wpf.Ui.Controls;

namespace VMCreate
{
    public partial class SuccessWindow : FluentWindow
    {
        public SuccessWindow()
        {
            InitializeComponent();
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}