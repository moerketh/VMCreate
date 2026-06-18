using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV.VmCreation;

namespace VMCreate
{
    /// <summary>
    /// Thin dispatcher that validates inputs and delegates execution to the
    /// VM deployment orchestrator. The immutable plan is supplied by the caller,
    /// so this class no longer mutates <see cref="VmSettings"/>.
    /// </summary>
    public class HyperVVmCreator : IVmCreator
    {
        private readonly IVmDeploymentOrchestrator _orchestrator;
        private readonly ILogger<HyperVVmCreator> _logger;

        public HyperVVmCreator(
            IVmDeploymentOrchestrator orchestrator,
            ILogger<HyperVVmCreator> logger)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<VmDeploymentResult> CreateAsync(
            VmDeploymentPlan plan,
            VmCustomizations vmCustomizations,
            GalleryItem galleryItem,
            CancellationToken cancellationToken = default,
            IProgress<CreateVMProgressInfo> progress = null,
            string sourceFile = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (vmCustomizations == null) throw new ArgumentNullException(nameof(vmCustomizations));
            if (galleryItem == null) throw new ArgumentNullException(nameof(galleryItem));

            if (string.IsNullOrWhiteSpace(plan.VmName))
                throw new ArgumentException("VM Name is required.", nameof(plan));
            if (string.IsNullOrWhiteSpace(galleryItem.DiskUri))
                throw new ArgumentException("Gallery item is missing a disk URI.", nameof(galleryItem));

            _logger.LogInformation(
                "Starting VM deployment for {VmName} from gallery item {GalleryItem}",
                plan.VmName, galleryItem.Name);

            progress?.Report(CreateVMProgressInfo.ForPhase(
                VmDeploymentPhase.None,
                VmDeploymentSubStep.None,
                plan.VmName));

            string effectiveSourceFile = string.IsNullOrWhiteSpace(sourceFile)
                ? galleryItem.DiskUri
                : sourceFile;

            try
            {
                await _orchestrator.DeployAsync(plan, vmCustomizations, galleryItem, cancellationToken, progress, effectiveSourceFile);
                return new VmDeploymentResult(plan.VmName, success: true);
            }
            catch (Exception ex)
            {
                return new VmDeploymentResult(plan.VmName, success: false, errorMessage: ex.Message);
            }
        }
    }
}
