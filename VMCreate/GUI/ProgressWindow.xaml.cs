using System;
using System.ComponentModel;
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
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = Math.Min(createVMProgressInfo.ProgressPercentage, 100);
            PercentText.Text = $"{createVMProgressInfo.ProgressPercentage}%";
            if (createVMProgressInfo.DownloadSpeed >= 0)
            {
                SpeedText.Text = $"Download Speed: {createVMProgressInfo.DownloadSpeed:F2} MB/s";
                SpeedText.Visibility = Visibility.Visible;
            }
            else { SpeedText.Visibility = Visibility.Collapsed; }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                // Don't close the window — cancel the operation instead.
                e.Cancel = true;
                RequestCancel();
                return;
            }
            base.OnClosing(e);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            RequestCancel();
        }

        private void RequestCancel()
        {
            if (_cancellationTokenSource.IsCancellationRequested) return;
            _cancellationTokenSource.Cancel();
            CancelButton.IsEnabled = false;
            StatusText.Text = "Cancelling\u2026";
            PercentText.Text = string.Empty;
            ProgressBar.IsIndeterminate = true;
        }
    }
}