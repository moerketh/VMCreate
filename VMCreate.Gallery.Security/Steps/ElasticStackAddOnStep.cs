using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Optional add-on card: installs the same Elastic SIEM stack as the
    /// appliance item, but onto an existing Debian-family distribution
    /// (Ubuntu, Debian, Kali, Parrot) selected in the gallery. Default-off —
    /// installing a full SIEM is an explicit choice. Shares the idempotent
    /// engine with <see cref="ElasticStackInstallStep"/> so a VM can be
    /// re-provisioned without duplicating work.
    /// </summary>
    public class ElasticStackAddOnStep : IConfigurableCustomizationStep, IDistributionOptionMetadata
    {
        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "Install Elastic SIEM (add-on)";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 150;

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => IsVisibleFor(item)
               && customizations?.DistributionOptions.Any(o => string.Equals(o.Name, Name, StringComparison.OrdinalIgnoreCase) && o.IsEnabled) == true;

        public Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
            => ElasticStackInstaller.ExecuteAsync(shell, item, customizations, logger, ct);

        // ── IStepVisibility ─────────────────────────────────────────────
        // apt-family only for now: the add-on install path is apt-based (the
        // rpm appliance path is handled by ElasticStackInstallStep). Grows to
        // include Rocky when the installer gains its dnf branch. The
        // appliance item itself is excluded (its required step installs).
        public bool IsVisibleFor(GalleryItem? item)
            => !item.HasTag("elastic-siem")
               && (item?.LinuxDistro is LinuxDistro.Ubuntu
                              or LinuxDistro.Debian
                              or LinuxDistro.Kali
                              or LinuxDistro.Parrot);

        // ── IConfigurableCustomizationStep (UI metadata) ────────────────
        public string CardTitle => "Elastic SIEM";
        public string CardDescription => "Turn this VM into a headless Elastic SIEM (Elasticsearch + Kibana + Fleet server).";
        public string Label => "Install the Elastic Stack (SIEM)";
        public string Tooltip => "Installs Elasticsearch, Kibana and an Elastic Agent Fleet server from the official Elastic 9.x apt repository. Idempotent: safe to re-run. Kibana will be reachable on http://<vm-ip>:5601; credentials are stored on the VM at /opt/vmcreate-elastic/credentials.txt.";
        public bool DefaultEnabled => false;
        public bool IsOptional => true;

        // ── IDistributionOptionMetadata (deploy-phase UI) ───────────────
        public string DeployTitle => "Install Elastic SIEM";
        public string DeployDescription => "Installing Elasticsearch, Kibana and a Fleet server";
        public string DeployPhaseId => "Sub_ElasticStackAddOn";
        public string DeployIconName => "ShieldTask24";
        public string? DeployCompletionInfo =>
            "Elastic SIEM ready — Kibana listens on http://<vm-ip>:5601; credentials are on the guest at /opt/vmcreate-elastic/credentials.txt";
    }
}
