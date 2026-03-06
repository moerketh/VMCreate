using System.Diagnostics;
using System.Windows;
using Wpf.Ui.Controls;

namespace VMCreate
{
    public partial class SuccessWindow : FluentWindow
    {
        private readonly string _vmName;

        public SuccessWindow(string vmName)
        {
            InitializeComponent();
            _vmName = vmName;
            SuccessMessage.Text = $"VM \u2018{vmName}\u2019 created successfully!";
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("vmconnect.exe", $"localhost \"{_vmName}\"")
            {
                UseShellExecute = true
            });
            Close();
        }

        private void HyperVManagerButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("mmc.exe", "virtmgmt.msc")
            {
                UseShellExecute = true
            });
            Close();
        }
    }
}