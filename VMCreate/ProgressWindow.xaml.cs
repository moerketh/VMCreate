using System;
using System.Windows;

namespace VMCreateVM
{
    public partial class ProgressWindow : Window
    {
        public bool IsCancelled { get; private set; }

        public ProgressWindow()
        {
            InitializeComponent();
            IsCancelled = false;
        }

        public void SetStatus(string status, string link)
        {
            StatusText.Text = status;
            PercentText.Text = "0%";
            if (string.IsNullOrEmpty(link))
            {
                LinkText.Visibility = Visibility.Hidden;
                SpeedText.Visibility = Visibility.Hidden;
            }
            else
            {
                LinkText.Text = link.Length > 100 ? link.Substring(0, 97) + "..." : link;
                LinkText.Visibility = Visibility.Visible;
                SpeedText.Visibility = Visibility.Visible;
            }
            ProgressBar.Value = 0;
            CancelButton.IsEnabled = status == "Downloading...";
        }

        public void UpdateProgress(double progress, double speedMBps)
        {
            ProgressBar.Value = Math.Min(progress, 100);
            PercentText.Text = $"{Math.Floor(progress)}%";
            if (speedMBps >= 0)
            {
                SpeedText.Text = $"Download Speed: {speedMBps:F2} MB/s";
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            IsCancelled = true;
            CancelButton.IsEnabled = false;
        }
    }
}