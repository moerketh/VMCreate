using System;
using System.Threading;
using System.Windows;
using Wpf.Ui.Controls;

namespace VMCreate
{
    public partial class ProgressWindow : FluentWindow
    {
        private readonly CancellationTokenSource _cancellationTokenSource;

        public ProgressWindow(CancellationTokenSource cts)
        {
            InitializeComponent();
            _cancellationTokenSource = cts;
        }

        public void UpdateProgress(CreateVMProgressInfo createVMProgressInfo)
        {
            StatusText.Text = createVMProgressInfo.Phase;
            LinkText.Text = createVMProgressInfo.URI;
            ProgressBar.Value = Math.Min(createVMProgressInfo.ProgressPercentage, 100);
            PercentText.Text = $"{createVMProgressInfo.ProgressPercentage}%";
            if (createVMProgressInfo.DownloadSpeed >= 0)
            {
                SpeedText.Text = $"Download Speed: {createVMProgressInfo.DownloadSpeed:F2} MB/s";
            }
            else { SpeedText.Visibility = Visibility.Hidden; }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cancellationTokenSource.Cancel();
            CancelButton.IsEnabled = false;
        }
    }
}