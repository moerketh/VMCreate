using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    /// <summary>
    /// Display model for VPN key download status shown in the UI.
    /// </summary>
    public class VpnKeyStatusItem
    {
        public string StatusIcon { get; set; } = "";
        public string Name { get; set; } = "";
        public string StatusText { get; set; } = "";
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
        private readonly IDesktopEnvironmentCatalog? _desktopCatalog;
        private RdpBackend _selectedRdpBackend = RdpBackend.Auto;
        private string? _selectedDesktopEnvironmentId;
        private string _htbApiToken = "";
        private bool _isDownloading;
        private string _ovpnFilePath = "";
        private bool _syncTimezone;
        private bool _useCustomSshKey;
        private string _customSshPublicKeyPath = "";
        private bool _enableIntegrationServices = true;
        private bool _isCustomDns;
        private string _customNameservers = "";
        private bool _elasticAiEnabled;
        private bool _elasticAiStartTrial = true;

        /// <summary>Raised when the wizard should complete (Finished or Canceled).</summary>
        public event Action<WizardResult>? RequestWizardComplete;

        /// <summary>Raised when the user clicks Back.</summary>
        public event Action? RequestNavigateBack;

        public VmCustomizationPageViewModel(
            WizardData wizardData, IHtbApiClient htbApiClient, ILogger logger,
            IEnumerable<IConfigurableCustomizationStep> configurableSteps,
            IEnumerable<IDesktopEnvironmentCatalog>? desktopCatalogs = null)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _htbApiClient = htbApiClient ?? throw new ArgumentNullException(nameof(htbApiClient));

            // Resolve the desktop catalog for the selected image (null when
            // the distro offers no desktop switching — card hidden).
            _desktopCatalog = desktopCatalogs.ForItem(wizardData.SelectedItem);
            if (_desktopCatalog != null)
            {
                foreach (var option in _desktopCatalog.Options)
                    DesktopOptions.Add(new DesktopOptionItem(option));
            }

            // Build the list of distribution-specific options visible for the selected image.
            var selectedItem = wizardData.SelectedItem;
            if (selectedItem != null && configurableSteps != null)
            {
                foreach (var step in configurableSteps.Where(s => s.IsVisibleFor(selectedItem) && s.IsOptional))
                    DistributionOptions.Add(new DistributionOptionItem(step));
            }

            // Seed from Customizations (pre-filled from the user's stored
            // preferences) and the stored HTB token so credentials are never
            // re-typed. Editing the token here overrides the stored one for
            // this run only.
            _selectedRdpBackend = wizardData.Customizations.RdpBackend;
            _isCustomDns = wizardData.Customizations.DnsMode == DnsMode.Custom;
            _customNameservers = wizardData.Customizations.CustomNameservers ?? "";
            _syncTimezone = wizardData.Customizations.SyncTimezone;
            _enableIntegrationServices = wizardData.Customizations.EnableIntegrationServices;
            _htbApiToken = wizardData.Preferences.HtbApiToken ?? "";

            FinishCommand = new RelayCommand(OnFinish);
            BackCommand = new RelayCommand(() => RequestNavigateBack?.Invoke());
            CancelCommand = new RelayCommand(() => RequestWizardComplete?.Invoke(WizardResult.Canceled));
            SelectDesktopCommand = new RelayCommand<string>(id => SelectDesktop(string.IsNullOrEmpty(id) ? null : id));
            DownloadVpnKeysCommand = new RelayCommand(
                () => _ = DownloadVpnKeysAsync(),
                () => !_isDownloading && !string.IsNullOrWhiteSpace(_htbApiToken));
            BrowseOvpnCommand = new RelayCommand(OnBrowseOvpn);
            BrowseSshKeyCommand = new RelayCommand(OnBrowseSshKey);
        }

        public GalleryItem? SelectedItem => _wizardData.SelectedItem;

        /// <summary>
        /// The RDP server backend selected by the user: Auto (default,
        /// detected in the guest at post-boot), xrdp (disables Wayland),
        /// Lamco (Wayland-native), or None. Bound to a 4-way radio group
        /// in the Remote Access card. <see cref="LamcoOptionVisible"/>
        /// hides the Lamco radio on unsupported distros.
        /// </summary>
        public RdpBackend SelectedRdpBackend
        {
            get => _selectedRdpBackend;
            set => SetProperty(ref _selectedRdpBackend, value);
        }

        // Back-compat boolean view over SelectedRdpBackend for the Auto radio.
        public bool IsRdpBackendAuto
        {
            get => _selectedRdpBackend == RdpBackend.Auto;
            set { if (value) SelectedRdpBackend = RdpBackend.Auto; }
        }

        public bool IsRdpBackendXrdp
        {
            get => _selectedRdpBackend == RdpBackend.Xrdp;
            set { if (value) SelectedRdpBackend = RdpBackend.Xrdp; }
        }

        public bool IsRdpBackendLamco
        {
            get => _selectedRdpBackend == RdpBackend.Lamco;
            set { if (value) SelectedRdpBackend = RdpBackend.Lamco; }
        }

        public bool IsRdpBackendWayrdp
        {
            get => _selectedRdpBackend == RdpBackend.Wayrdp;
            set { if (value) SelectedRdpBackend = RdpBackend.Wayrdp; }
        }

        public bool IsRdpBackendNone
        {
            get => _selectedRdpBackend == RdpBackend.None;
            set { if (value) SelectedRdpBackend = RdpBackend.None; }
        }

        /// <summary>
        /// True when the selected gallery item supports the Lamco RDP Server
        /// backend (Debian family: Ubuntu, Debian, Parrot — the install is a
        /// pinned amd64 Debian deb). Hides the Lamco radio on unsupported
        /// distros. Evaluated from the
        /// <see cref="GalleryItem.LinuxDistro"/> metadata hint (no live shell yet).
        /// </summary>
        public bool LamcoOptionVisible => SelectedItem?.SupportsLamco() ?? false;

        /// <summary>
        /// True when the wayrdp radio should render: a wayrdp-targetable
        /// gallery item (Debian-family or Arch-family — the install curls a
        /// pinned public release asset, so the backend works from the GUI
        /// end-to-end). Same shape as <see cref="LamcoOptionVisible"/>.
        /// </summary>
        public bool WayrdpOptionVisible => SelectedItem?.SupportsWayrdp() ?? false;

        // ── Desktop Environment card ────────────────────────────────────

        /// <summary>
        /// The desktop options offered by the selected image's catalog
        /// (empty when the distro ships no catalog — card hidden).
        /// </summary>
        public ObservableCollection<DesktopOptionItem> DesktopOptions { get; } = new();

        /// <summary>
        /// True when the Desktop Environment card should render: a Linux
        /// image with a desktop catalog.
        /// </summary>
        public bool ShowDesktopOptions => _desktopCatalog != null && ShowLinuxOptions;

        /// <summary>Name of the image's stock desktop, for the keep-default radio.</summary>
        public string DefaultDesktopName => _desktopCatalog?.Default?.DisplayName ?? "default";

        /// <summary>
        /// The desktop that will actually be active after deployment: the
        /// selected one, or the catalog default when none was picked. Drives
        /// the RDP-backend coupling (Lamco needs a Lamco-capable desktop).
        /// </summary>
        public DesktopEnvironmentOption? EffectiveDesktop
        {
            get
            {
                if (_desktopCatalog == null)
                    return null;
                foreach (var option in _desktopCatalog.Options)
                    if (option.Id == _selectedDesktopEnvironmentId)
                        return option;
                return _desktopCatalog.Default;
            }
        }

        /// <summary>
        /// True when the Lamco radio is selectable: the distro supports
        /// Lamco AND the effective desktop is Lamco-capable (Wayland session
        /// + supported portal stack). An X11-only desktop (e.g. Xfce) can
        /// never be served by the Wayland-native Lamco server.
        /// </summary>
        public bool LamcoOptionEnabled =>
            LamcoOptionVisible
            && (_desktopCatalog == null || (EffectiveDesktop?.LamcoSupported ?? false));

        /// <summary>
        /// True when the Auto-backend hint should render: only meaningful
        /// when a desktop catalog influences the outcome.
        /// </summary>
        public bool ShowAutoRdpHint => ShowDesktopOptions && LamcoOptionVisible;

        /// <summary>
        /// Predictive hint under the Auto radio: what Auto will resolve to
        /// given the effective desktop (X11-only desktops always land on
        /// xrdp regardless of detection).
        /// </summary>
        public string AutoRdpHint
        {
            get
            {
                if (!LamcoOptionVisible)
                    return "";
                return LamcoOptionEnabled
                    ? "Auto will detect the session after boot — typically the Lamco RDP Server for this desktop."
                    : "This desktop is X11-only — Auto will install xrdp (the Lamco RDP Server needs Wayland).";
            }
        }

        /// <summary>
        /// The selected desktop id (null = keep the stock desktop). Set by
        /// the radio group via <see cref="SelectDesktop"/>; changing it
        /// re-evaluates the Lamco coupling: an explicit Lamco choice that
        /// becomes impossible is reset to Auto rather than failing at
        /// deploy time.
        /// </summary>
        public string? SelectedDesktopEnvironmentId
        {
            get => _selectedDesktopEnvironmentId;
            private set
            {
                if (SetProperty(ref _selectedDesktopEnvironmentId, value))
                {
                    OnPropertyChanged(nameof(EffectiveDesktop));
                    OnPropertyChanged(nameof(LamcoOptionEnabled));
                    OnPropertyChanged(nameof(AutoRdpHint));
                    OnPropertyChanged(nameof(ShowAutoRdpHint));
                    if (!LamcoOptionEnabled && _selectedRdpBackend == RdpBackend.Lamco)
                        SelectedRdpBackend = RdpBackend.Auto;
                }
            }
        }

        /// <summary>
        /// Radio-group setter: the keep-default radio passes null; each
        /// option radio passes its id and flips its IsSelected flag.
        /// </summary>
        public void SelectDesktop(string? desktopId)
        {
            SelectedDesktopEnvironmentId = desktopId;
            foreach (var option in DesktopOptions)
                option.IsSelected = option.Id == desktopId;
            OnPropertyChanged(nameof(IsKeepDefaultDesktop));
        }

        /// <summary>Bound to the keep-default radio; true when no switch was picked.</summary>
        public bool IsKeepDefaultDesktop
        {
            get => _selectedDesktopEnvironmentId == null;
            set
            {
                if (value)
                    SelectDesktop(null);
            }
        }

        // Legacy binding kept for any XAML still referencing ConfigureXrdp.
        public bool ConfigureXrdp
        {
            get => _selectedRdpBackend == RdpBackend.Xrdp;
            set => SelectedRdpBackend = value ? RdpBackend.Xrdp : RdpBackend.None;
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

        public bool EnableIntegrationServices
        {
            get => _enableIntegrationServices;
            set => SetProperty(ref _enableIntegrationServices, value);
        }

        /// <summary>
        /// True for non-Windows images. Linux-only cards (Remote Access/xrdp, DNS, SSH key, HTB VPN)
        /// are hidden for Windows VMs, which are configured via PowerShell Direct rather than SSH.
        /// </summary>
        public bool ShowLinuxOptions => SelectedItem?.IsWindows != true;

        public bool IsHostDns
        {
            get => !_isCustomDns;
            set
            {
                if (value && SetProperty(ref _isCustomDns, false))
                    OnPropertyChanged(nameof(IsCustomDns));
            }
        }

        public bool IsCustomDns
        {
            get => _isCustomDns;
            set
            {
                if (SetProperty(ref _isCustomDns, value))
                    OnPropertyChanged(nameof(IsHostDns));
            }
        }

        public string CustomNameservers
        {
            get => _customNameservers;
            set => SetProperty(ref _customNameservers, value);
        }

        public bool IsSshKeyPathEnabled => _useCustomSshKey;

        /// <summary>True for security distributions (e.g. Kali, Parrot, PwnCloudOS). Windows
        /// security distributions are excluded — HTB VPN provisioning is SSH/Linux-based.</summary>
        public bool ShowHtbVpn => SelectedItem?.IsSecurity == true && SelectedItem?.IsWindows != true;

        /// <summary>
        /// True when an Elastic SIEM step targets this image (the appliance
        /// item's required step, or the optional add-on card) AND a complete
        /// LLM configuration is stored — the AI features read their endpoint
        /// and key from the machine-level settings, never per-VM input.
        /// </summary>
        public bool ShowElasticAi =>
            SelectedItem != null
            && (SelectedItem.HasTag("elastic-siem")
                || DistributionOptions.Any(o => o.Name.Equals("Install Elastic SIEM (add-on)", StringComparison.OrdinalIgnoreCase)))
            && _wizardData.Preferences.HasLlmConfiguration;

        /// <summary>Enable AI features on the deployed Elastic Stack.</summary>
        public bool ElasticAiEnabled
        {
            get => _elasticAiEnabled;
            set => SetProperty(ref _elasticAiEnabled, value);
        }

        /// <summary>
        /// Start a 30-day Enterprise trial license (required for the AI
        /// features on a self-hosted stack). Default on when AI is enabled.
        /// </summary>
        public bool ElasticAiStartTrial
        {
            get => _elasticAiStartTrial;
            set => SetProperty(ref _elasticAiStartTrial, value);
        }

        /// <summary>Distribution-specific options discovered from plugin assemblies.</summary>
        public ObservableCollection<DistributionOptionItem> DistributionOptions { get; } = new();

        /// <summary>True when the selected image is not in VHDX format and needs conversion.
        /// Native Hyper-V images (e.g. Windows) are already in VHDX format and need no conversion.</summary>
        public bool IsNotVhdx =>
            _wizardData.SelectedItem?.IsNativeHyperV != true
            && DiskFileDetector.DetectFileType(_wizardData.SelectedItem?.DiskUri) != DiskImageFormat.Vhdx;

        /// <summary>
        /// True when the selected image is a native Hyper-V Windows VM that requires
        /// an elevated unattend injection (UAC prompt) before first boot.
        /// </summary>
        public bool NeedsUnattendInjection =>
            _wizardData.SelectedItem?.IsNativeHyperV == true
            && _wizardData.SelectedItem?.IsWindows == true;

        public ICommand FinishCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand CancelCommand { get; }
        /// <summary>Radio-group command: selects a desktop by id (null/"" = keep default).</summary>
        public ICommand SelectDesktopCommand { get; }
        public ICommand DownloadVpnKeysCommand { get; }
        public ICommand BrowseOvpnCommand { get; }
        public ICommand BrowseSshKeyCommand { get; }

        private void OnFinish()
        {
            // Normalize: picking the stock desktop explicitly is the same as
            // keeping the default — no switch step should run for it.
            bool keepDefault = _selectedDesktopEnvironmentId == null
                || _selectedDesktopEnvironmentId.Equals(
                    _desktopCatalog?.Default?.Id, StringComparison.OrdinalIgnoreCase);

            _wizardData.Customizations.DesktopEnvironmentId = keepDefault
                ? null
                : _selectedDesktopEnvironmentId;

            // The desktop/RDP coupling is enforced at selection time (an
            // X11-only desktop resets an explicit Lamco choice to Auto);
            // re-check defensively in case state was set programmatically.
            if (!LamcoOptionEnabled && _selectedRdpBackend == RdpBackend.Lamco)
            {
                _logger.LogWarning(
                    "Explicit Lamco RDP backend with a non-Lamco desktop — resetting to Auto.");
                _selectedRdpBackend = RdpBackend.Auto;
            }

            _wizardData.Customizations.RdpBackend = _selectedRdpBackend;
            _wizardData.Customizations.HtbVpnKeys = new List<HtbVpnKey>(_downloadedKeys);
            _wizardData.Customizations.OvpnFilePath = _ovpnFilePath;
            _wizardData.Customizations.ConfigureHtbVpn =
                _downloadedKeys.Count > 0 || !string.IsNullOrEmpty(_ovpnFilePath);
            _wizardData.Customizations.SyncTimezone = _syncTimezone;
            _wizardData.Customizations.CustomSshPublicKeyPath = _useCustomSshKey ? _customSshPublicKeyPath : null;
            _wizardData.Customizations.EnableIntegrationServices = _enableIntegrationServices;
            _wizardData.Customizations.DnsMode = _isCustomDns ? DnsMode.Custom : DnsMode.Host;
            _wizardData.Customizations.CustomNameservers = _isCustomDns ? _customNameservers : null;
            // The token used this run — DeployPage persists it into the
            // preferences store on success so the user types it exactly once.
            _wizardData.LastUsedHtbApiToken = string.IsNullOrWhiteSpace(_htbApiToken) ? null : _htbApiToken;

            _wizardData.Customizations.DistributionOptions.Clear();
            foreach (var opt in DistributionOptions.Where(o => o.IsEnabled))
            {
                _wizardData.Customizations.DistributionOptions.Add(new DistributionOptionSelection
                {
                    Name = opt.Name,
                    IsEnabled = true
                });
            }

            // AI enablement: values come from the machine-level credential
            // store (never typed per-VM); this only records the choice.
            _wizardData.Customizations.ElasticAi = new ElasticAiSettings
            {
                Enabled = ElasticAiEnabled,
                StartTrialLicense = ElasticAiStartTrial,
                ChatCompletionsUrl = _wizardData.Preferences.LlmChatCompletionsUrl,
                ApiKey = _wizardData.Preferences.LlmApiKey,
                Model = _wizardData.Preferences.LlmModel,
            };

            _logger.LogDebug(
                "Finished customization: RdpBackend={RdpBackend}, Desktop={Desktop}, HtbVpnKeys={KeyCount}, ManualOvpn={ManualPath}, SyncTimezone={Tz}, CustomKey={Key}, IntegrationServices={IntSvc}, DnsMode={DnsMode}, DistOptions={DistOpts}",
                _selectedRdpBackend, _wizardData.Customizations.DesktopEnvironmentId ?? "(default)",
                _downloadedKeys.Count, _ovpnFilePath, _syncTimezone, _useCustomSshKey, _enableIntegrationServices,
                _isCustomDns ? "Custom" : "Host",
                string.Join(", ", DistributionOptions.Select(o => $"{o.Name}={o.IsEnabled}")));

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
                    if (result.Success && result.Key != null)
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
                            StatusText = result.ErrorMessage ?? "Unknown error"
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
