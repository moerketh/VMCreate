using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Required infrastructure step for the Elastic SIEM appliance gallery
    /// item: installs the full Elastic Stack (Elasticsearch + Kibana + Fleet
    /// server) on the freshly cloud-init-booted Ubuntu Server VM. Not shown as
    /// an optional card — deploying this item means deploying the SIEM. The
    /// optional add-on variant for other distributions is
    /// <see cref="ElasticStackAddOnStep"/>; both share
    /// <see cref="ElasticStackInstaller"/>.
    /// </summary>
    public class ElasticStackInstallStep : ICustomizationStep, IStepVisibility, IDistributionOptionMetadata
    {
        // ── ICustomizationStep ──────────────────────────────────────────
        public string Name => "Install Elastic SIEM";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 50;

        public string? ProgressPhaseId => (this as IDistributionOptionMetadata)?.DeployPhaseId;

        public bool IsApplicable(GalleryItem? item, VmCustomizations? customizations)
            => IsVisibleFor(item); // required: no option selection needed

        public Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, System.Threading.CancellationToken ct)
            => ElasticStackInstaller.ExecuteAsync(shell, item, customizations, logger, ct);

        // ── IStepVisibility ─────────────────────────────────────────────
        public bool IsVisibleFor(GalleryItem? item) => item.HasTag("elastic-siem");

        // ── IDistributionOptionMetadata (deploy-phase UI) ───────────────
        public string DeployTitle => "Install Elastic SIEM";
        public string DeployDescription => "Installing Elasticsearch, Kibana and a Fleet server";
        public string DeployPhaseId => "Sub_ElasticStackInstall";
        public string DeployIconName => "ShieldTask24";
        public string? DeployCompletionInfo =>
            "Elastic SIEM ready — Kibana listens on http://<vm-ip>:5601; credentials are on the guest at /opt/vmcreate-elastic/credentials.txt";
    }
}
