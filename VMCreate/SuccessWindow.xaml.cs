using System.Windows;

namespace VMCreateVM
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