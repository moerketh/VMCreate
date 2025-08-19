using System.Windows;

namespace VMCreate
{
    public partial class SuccessWindow : Window
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