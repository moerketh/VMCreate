using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.MediaHandlers;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Coordinates the full VM deployment flow: media preparation, strategy selection,
    /// ISO provisioning, cleanup on failure, and replacement of previous VMs.
    /// The thin <see cref="IVmCreator"/> implementation delegates here so that the
    /// orchestrator can be tested and evolved independently of the entry contract.
    /// </summary>
    public interface IVmDeploymentOrchestrator
    {
        /// <summary>
        /// Prepares media, selects a strategy, and deploys the VM from the supplied plan.
        /// </summary>
        Task<VmDeploymentResult> DeployAsync(
            VmDeploymentPlan plan,
            VmCustomizations customizations,
            GalleryItem galleryItem,
            CancellationToken cancellationToken,
            IProgress<CreateVMProgressInfo> progress,
            string sourceFile);
    }
}
