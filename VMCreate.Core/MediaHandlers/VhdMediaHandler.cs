using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VMCreate.MediaHandlers
{
    /// <summary>
    /// Handles fixed/dynamic VHD sources (e.g. Rocky's Hyper-V/Azure cloud
    /// images, published as <c>.vhdfixed.xz</c>): converts to VHDX via
    /// qemu-img (<c>vpc</c> format) and resolves the VM generation from the
    /// converted disk's partition layout.
    /// </summary>
    public class VhdMediaHandler : MediaHandler
    {
        private readonly IDiskConverter _diskConverter;
        private readonly IVmGenerationResolver _generationResolver;
        private int _vmGeneration;

        public VhdMediaHandler(
            ILogger<VhdMediaHandler> logger,
            IDiskConverter diskConverter,
            IVmGenerationResolver generationResolver)
            : base(logger)
        {
            _diskConverter = diskConverter ?? throw new ArgumentNullException(nameof(diskConverter));
            _generationResolver = generationResolver ?? throw new ArgumentNullException(nameof(generationResolver));
        }

        public override bool RequiresExtraction => false;

        public override DiskImageFormat FileType => DiskImageFormat.Vhd;

        public override int VmGeneration => _vmGeneration;

        public override async Task<MediaPreparationResult> PrepareMediaAsync(
            string sourceFile,
            string destinationPath,
            VmDeploymentPlan plan,
            GalleryItem item,
            IProgress<CreateVMProgressInfo> progressInfo,
            CancellationToken cancellationToken)
        {
            progressInfo.Report(new CreateVMProgressInfo { Phase = VmDeploymentPhase.Convert });
            string vhdxDestFile = Path.Combine(destinationPath, plan.VmName + ".vhdx");
            _logger.LogInformation("Converting VHD to VHDX: {VhdxDestFile}", vhdxDestFile);
            string convertedFile = await _diskConverter.ConvertToVhdxAsync(sourceFile, vhdxDestFile, progressInfo, cancellationToken);
            _logger.LogInformation("Converted VHD to VHDX: {ConvertedFile}", convertedFile);

            var resolution = await _generationResolver.ResolveAsync(convertedFile, plan, cancellationToken);
            _vmGeneration = resolution.VmGeneration;

            return new MediaPreparationResult(vhdxDestFile, _vmGeneration, resolution.DetectedVirtualSizeBytes);
        }
    }
}
