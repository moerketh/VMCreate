using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows.Input;

namespace VMCreate
{
    /// <summary>
    /// Display model for VPN key download status shown in the UI.
    /// </summary>
    public class VpnKeyStatusItem
    {
        public string StatusIcon { get; set; }
        public string Name { get; set; }
        public string StatusText { get; set; }
    }

    /// <summary>
    /// ViewModel for the VM customization page.
    /// Manages post-install options (e.g. xrdp, HTB VPN, timezone sync) and conversion banner visibility.
    /// </summary>
    public class VmCustomizationPageViewModel : ViewModelBase
    {
        private readonly WizardData _wizardData;
        private readonly ILogger _logger;
        private readonly IHtbApiClient _htbApiClient;
        private readonly List<HtbVpnKey> _downloadedKeys = new();
        private bool _configureXrdp = true;
        private string _htbApiToken;
        private bool _isDownloading;
        private string _ovpnFilePath;
        private bool _syncTimezone;
        private bool _useCustomSshKey;
        private string _customSshPublicKeyPath;

        /// <summary>Raised when the wizard should complete (Finished or Canceled).</summary>
        public event Action<WizardResult> RequestWizardComplete;

        /// <summary>Raised when the user clicks Back.</summary>
        public event Action RequestNavigateBack;

        public VmCustomizationPageViewModel(WizardData wizardData, IHtbApiClient htbApiClient, ILogger logger)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _htbApiClient = htbApiClient ?? throw new ArgumentNullException(nameof(htbApiClient));

            FinishCommand = new RelayCommand(OnFinish);
            BackCommand = new RelayCommand(() => RequestNavigateBack?.Invoke());
            CancelCommand = new RelayCommand(() => RequestWizardComplete?.Invoke(WizardResult.Canceled));
            DownloadVpnKeysCommand = new RelayCommand(
                () => _ = DownloadVpnKeysAsync(),
                () => !_isDownloading && !string.IsNullOrWhiteSpace(_htbApiToken));
            BrowseOvpnCommand = new RelayCommand(OnBrowseOvpn);
            BrowseSshKeyCommand = new RelayCommand(OnBrowseSshKey);
        }

        public GalleryItem SelectedItem => _wizardData.SelectedItem;

        public bool ConfigureXrdp
        {
            get => _configureXrdp;
            set => SetProperty(ref _configureXrdp, value);
        }

        public string HtbApiToken
        {
            get => _htbApiToken;
            set
            {
                if (SetProperty(ref _htbApiToken, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            set
            {
                if (SetProperty(ref _isDownloading, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public ObservableCollection<VpnKeyStatusItem> VpnKeyStatuses { get; } = new();

        public string OvpnFilePath
        {
            get => _ovpnFilePath;
            set => SetProperty(ref _ovpnFilePath, value);
        }

        public bool SyncTimezone
        {
            get => _syncTimezone;
            set => SetProperty(ref _syncTimezone, value);
        }

        public bool UseCustomSshKey
        {
            get => _useCustomSshKey;
            set
            {
                if (SetProperty(ref _useCustomSshKey, value))
                    OnPropertyChanged(nameof(IsSshKeyPathEnabled));
            }
        }

        public string CustomSshPublicKeyPath
        {
            get => _customSshPublicKeyPath;
            set => SetProperty(ref _customSshPublicKeyPath, value);
        }

        public bool IsSshKeyPathEnabled => _useCustomSshKey;

        /// <summary>True when the selected image is not in VHDX format (requires conversion).</summary>
        public bool IsNotVhdx =>
            !string.Equals(_wizardData.SelectedItem?.FileType, "VHDX", StringComparison.OrdinalIgnoreCase);

        public ICommand FinishCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand DownloadVpnKeysCommand { get; }
        public ICommand BrowseOvpnCommand { get; }
        public ICommand BrowseSshKeyCommand { get; }

        private void OnFinish()
        {
            _wizardData.Customizations.ConfigureXrdp = _configureXrdp;
            _wizardData.Customizations.HtbVpnKeys = new List<HtbVpnKey>(_downloadedKeys);
            _wizardData.Customizations.OvpnFilePath = _ovpnFilePath;
            _wizardData.Customizations.ConfigureHtbVpn =
                _downloadedKeys.Count > 0 || !string.IsNullOrEmpty(_ovpnFilePath);
            _wizardData.Customizations.SyncTimezone = _syncTimezone;
            _wizardData.Customizations.CustomSshPublicKeyPath = _useCustomSshKey ? _customSshPublicKeyPath : null;

            _logger.LogDebug(
                "Finished customization: ConfigureXrdp={Xrdp}, HtbVpnKeys={KeyCount}, ManualOvpn={ManualPath}, SyncTimezone={Tz}, CustomKey={Key}",
                _configureXrdp, _downloadedKeys.Count, _ovpnFilePath, _syncTimezone, _useCustomSshKey);

            RequestWizardComplete?.Invoke(WizardResult.Finished);
        }

        private async System.Threading.Tasks.Task DownloadVpnKeysAsync()
        {
            if (string.IsNullOrWhiteSpace(_htbApiToken))
                return;

            IsDownloading = true;
            VpnKeyStatuses.Clear();
            _downloadedKeys.Clear();

            // Show pending status for each known endpoint
            foreach (var name in _htbApiClient.EndpointNames)
            {
                VpnKeyStatuses.Add(new VpnKeyStatusItem
                {
                    StatusIcon = "\u23F3",
                    Name = name,
                    StatusText = "Downloading..."
                });
            }

            try
            {
                var results = await _htbApiClient.DownloadAllKeysAsync(_htbApiToken, CancellationToken.None);

                VpnKeyStatuses.Clear();
                foreach (var result in results)
                {
                    if (result.Success)
                    {
                        _downloadedKeys.Add(result.Key);
                        VpnKeyStatuses.Add(new VpnKeyStatusItem
                        {
                            StatusIcon = "\u2714",
                            Name = result.EndpointName,
                            StatusText = $"Downloaded ({result.Key.OvpnContent.Length:N0} bytes)"
                        });
                    }
                    else
                    {
                        VpnKeyStatuses.Add(new VpnKeyStatusItem
                        {
                            StatusIcon = "\u2718",
                            Name = result.EndpointName,
                            StatusText = result.ErrorMessage
                        });
                    }
                }

                _logger.LogInformation("VPN key download complete: {Success}/{Total} successful",
                    _downloadedKeys.Count, results.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading VPN keys");
                VpnKeyStatuses.Clear();
                VpnKeyStatuses.Add(new VpnKeyStatusItem
                {
                    StatusIcon = "\u2718",
                    Name = "Error",
                    StatusText = ex.Message
                });
            }
            finally
            {
                IsDownloading = false;
            }
        }

        private void OnBrowseOvpn()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select HTB VPN configuration file",
                Filter = "OpenVPN files (*.ovpn)|*.ovpn|All files (*.*)|*.*",
                DefaultExt = ".ovpn"
            };

            if (dialog.ShowDialog() == true)
                OvpnFilePath = dialog.FileName;
        }

        private void OnBrowseSshKey()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select SSH public key file",
                Filter = "Public key files (*.pub)|*.pub|All files (*.*)|*.*",
                DefaultExt = ".pub"
            };

            if (dialog.ShowDialog() == true)
                CustomSshPublicKeyPath = dialog.FileName;
        }
    }
}
