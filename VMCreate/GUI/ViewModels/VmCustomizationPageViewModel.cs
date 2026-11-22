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
    /// ViewModel for the VM customization page.
    /// Manages post-install options (e.g. xrdp, HTB VPN, timezone sync) and conversion banner visibility.
    /// </summary>
    public class VmCustomizationPageViewModel : ViewModelBase
    {
        private readonly WizardData _wizardData;
        private readonly ILogger _logger;
        private readonly IDesktopEnvironmentCatalog? _desktopCatalog;
        private RdpBackend _selectedRdpBackend = RdpBackend.Auto;
        private string? _selectedDesktopEnvironmentId;
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
            WizardData wizardData, ILogger logger,
            IEnumerable<IConfigurableCustomizationStep> configurableSteps,
            IEnumerable<IDesktopEnvironmentCatalog>? desktopCatalogs = null)
        {
            _wizardData = wizardData ?? throw new ArgumentNullException(nameof(wizardData));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
            // preferences).
            _selectedRdpBackend = wizardData.Customizations.RdpBackend;
            NormalizeRdpBackend();
            _isCustomDns = wizardData.Customizations.DnsMode == DnsMode.Custom;
            _customNameservers = wizardData.Customizations.CustomNameservers ?? "";
            _syncTimezone = wizardData.Customizations.SyncTimezone;
            _enableIntegrationServices = wizardData.Customizations.EnableIntegrationServices;

            FinishCommand = new RelayCommand(OnFinish);
            BackCommand = new RelayCommand(() => RequestNavigateBack?.Invoke());
            CancelCommand = new RelayCommand(() => RequestWizardComplete?.Invoke(WizardResult.Canceled));
            SelectDesktopCommand = new RelayCommand<string>(id => SelectDesktop(string.IsNullOrEmpty(id) ? null : id));
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
            set
            {
                if (!SetProperty(ref _selectedRdpBackend, value))
                    return;
                RdpBackendNotice = null;
                // The radios bind to these views; a programmatic reset (e.g.
                // xrdp → Auto after picking GNOME) must move the check too.
                OnPropertyChanged(nameof(IsRdpBackendAuto));
                OnPropertyChanged(nameof(IsRdpBackendXrdp));
                OnPropertyChanged(nameof(IsRdpBackendLamco));
                OnPropertyChanged(nameof(IsRdpBackendWayrdp));
                OnPropertyChanged(nameof(IsRdpBackendNone));
                OnPropertyChanged(nameof(ConfigureXrdp));
            }
        }

        /// <summary>
        /// Why the RDP backend was just changed for the user (e.g. xrdp reset
        /// to Auto after picking GNOME); null when nothing was changed. Shown
        /// under the Remote Access radios, cleared by the next explicit pick.
        /// </summary>
        public string? RdpBackendNotice
        {
            get => _rdpBackendNotice;
            private set
            {
                if (SetProperty(ref _rdpBackendNotice, value))
                    OnPropertyChanged(nameof(HasRdpBackendNotice));
            }
        }
        private string? _rdpBackendNotice;

        public bool HasRdpBackendNotice => !string.IsNullOrEmpty(_rdpBackendNotice);

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
        /// gallery item (Omarchy, or Kali / Parrot for the KDE backend — the
        /// install curls a pinned public release asset, so the backend works
        /// from the GUI end-to-end). Same shape as <see cref="LamcoOptionVisible"/>.
        /// </summary>
        public bool WayrdpOptionVisible => SelectedItem?.SupportsWayrdp() ?? false;

        /// <summary>
        /// True when the wayrdp radio is selectable: the image supports it AND,
        /// on an image with a desktop catalog (Kali), the effective desktop is
        /// one wayrdp serves (KDE Plasma). Omarchy and catalog-less images
        /// (Parrot: KDE out of the box) need no desktop check.
        /// </summary>
        public bool WayrdpOptionEnabled =>
            WayrdpOptionVisible
            && (_desktopCatalog == null
                || SelectedItem.HasTag("omarchy")
                || (EffectiveDesktop?.WayrdpSupported ?? false));

        /// <summary>
        /// True when the xrdp radio is selectable: always on images without a
        /// desktop catalog (they keep the Force X11 fallback), otherwise only
        /// when the effective desktop is X11-only. KDE and GNOME run on
        /// Wayland; forcing them onto X11 for xrdp is not offered.
        /// </summary>
        public bool XrdpOptionEnabled =>
            _desktopCatalog == null || (EffectiveDesktop?.XrdpSupported ?? true);

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
                if (WayrdpOptionEnabled && EffectiveDesktop?.Id == "gnome"
                    && SelectedItem?.LinuxDistro.SupportsWayrdpGnome() == true)
                    return "This desktop is GNOME — Auto will install wayrdp (GNOME 50+; the Lamco RDP Server otherwise).";
                if (WayrdpOptionEnabled && SelectedItem?.LinuxDistro.SupportsWayrdpKde() == true)
                    return "This desktop is KDE Plasma — Auto will install wayrdp (Plasma 6.3+; the Lamco RDP Server otherwise).";
                return LamcoOptionEnabled
                    ? "This desktop runs on Wayland — Auto will install the Lamco RDP Server (xrdp needs X11)."
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
                    OnPropertyChanged(nameof(XrdpOptionEnabled));
                    OnPropertyChanged(nameof(WayrdpOptionEnabled));
                    OnPropertyChanged(nameof(AutoRdpHint));
                    OnPropertyChanged(nameof(ShowAutoRdpHint));
                    NormalizeRdpBackend();
                }
            }
        }

        /// <summary>
        /// Resets an RDP backend the image or the effective desktop cannot
        /// use to Auto, and says why (<see cref="RdpBackendNotice"/>), rather
        /// than letting the deployment refuse it later.
        /// </summary>
        private void NormalizeRdpBackend()
        {
            string? reason = _selectedRdpBackend switch
            {
                RdpBackend.Xrdp when !XrdpOptionEnabled =>
                    $"xrdp needs an X11 desktop and {EffectiveDesktop?.DisplayName} runs on Wayland, so Remote Access was set to Auto.",
                RdpBackend.Lamco when !LamcoOptionEnabled && LamcoOptionVisible =>
                    $"The Lamco RDP Server needs a Wayland desktop and {EffectiveDesktop?.DisplayName ?? "this desktop"} is X11-only, so Remote Access was set to Auto.",
                RdpBackend.Lamco when !LamcoOptionVisible =>
                    "The Lamco RDP Server is not available for this image, so Remote Access was set to Auto.",
                RdpBackend.Wayrdp when !WayrdpOptionVisible =>
                    "wayrdp supports Hyprland (Omarchy) and KDE Plasma on Kali and Parrot at this time, so Remote Access was set to Auto.",
                RdpBackend.Wayrdp when !WayrdpOptionEnabled =>
                    $"wayrdp needs KDE Plasma on this image and {EffectiveDesktop?.DisplayName ?? "this desktop"} is not, so Remote Access was set to Auto.",
                _ => null,
            };
            if (reason == null)
                return;
            _logger.LogInformation("RDP backend {Backend} reset to Auto: {Reason}", _selectedRdpBackend, reason);
            SelectedRdpBackend = RdpBackend.Auto;
            RdpBackendNotice = reason;
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
        public bool IsHtbVpnEligible => SelectedItem?.IsSecurity == true && SelectedItem?.IsWindows != true;

        /// <summary>
        /// True when HTB VPN configs will be deployed: an eligible image and
        /// an HTB App Token stored in Settings. There is no per-VM HTB input;
        /// the deploy step downloads the keys with the stored token.
        /// </summary>
        public bool WillConfigureHtbVpn =>
            IsHtbVpnEligible && !string.IsNullOrWhiteSpace(_wizardData.Preferences.HtbApiToken);

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

        /// <summary>True when the image has optional add-ons; the Add-Ons card is hidden otherwise.</summary>
        public bool HasDistributionOptions => DistributionOptions.Count > 0;

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

            // The desktop/RDP coupling is enforced at selection time (a
            // desktop the backend cannot serve resets it to Auto); re-check
            // defensively in case state was set programmatically. The
            // deployment refuses the same combinations.
            NormalizeRdpBackend();

            _wizardData.Customizations.RdpBackend = _selectedRdpBackend;
            _wizardData.Customizations.HtbVpnKeys = new List<HtbVpnKey>();
            _wizardData.Customizations.OvpnFilePath = null;
            _wizardData.Customizations.ConfigureHtbVpn = WillConfigureHtbVpn;
            _wizardData.Customizations.SyncTimezone = _syncTimezone;
            _wizardData.Customizations.CustomSshPublicKeyPath = _useCustomSshKey ? _customSshPublicKeyPath : null;
            _wizardData.Customizations.EnableIntegrationServices = _enableIntegrationServices;
            _wizardData.Customizations.DnsMode = _isCustomDns ? DnsMode.Custom : DnsMode.Host;
            _wizardData.Customizations.CustomNameservers = _isCustomDns ? _customNameservers : null;

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
                "Finished customization: RdpBackend={RdpBackend}, Desktop={Desktop}, HtbVpn={HtbVpn}, SyncTimezone={Tz}, CustomKey={Key}, IntegrationServices={IntSvc}, DnsMode={DnsMode}, DistOptions={DistOpts}",
                _selectedRdpBackend, _wizardData.Customizations.DesktopEnvironmentId ?? "(default)",
                _wizardData.Customizations.ConfigureHtbVpn, _syncTimezone, _useCustomSshKey, _enableIntegrationServices,
                _isCustomDns ? "Custom" : "Host",
                string.Join(", ", DistributionOptions.Select(o => $"{o.Name}={o.IsEnabled}")));

            RequestWizardComplete?.Invoke(WizardResult.Finished);
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
