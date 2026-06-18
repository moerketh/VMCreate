using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System;

namespace VMCreate.MediaHandlers
{
    public class Qcow2MediaHandler : MediaHandler
    {
        private readonly IDiskConverter _diskConverter;
        private readonly IVmGenerationResolver _generationResolver;
        private int _vmGeneration;

        public Qcow2MediaHandler(
            ILogger<Qcow2MediaHandler> logger,
            IDiskConverter diskConverter,
            IVmGenerationResolver generationResolver)
            : base(logger)
        {
            _diskConverter = diskConverter ?? throw new ArgumentNullException(nameof(diskConverter));
            _generationResolver = generationResolver ?? throw new ArgumentNullException(nameof(generationResolver));
        }

        public override bool RequiresExtraction => false;

        public override DiskImageFormat FileType => DiskImageFormat.Qcow2;

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
            string vhdDestFile = Path.Combine(destinationPath, plan.VmName + ".vhdx");
            _logger.LogInformation("Converting Qcow2 to VHDX: {VhdDestFile}", vhdDestFile);
            string convertedFile = await _diskConverter.ConvertToVhdxAsync(sourceFile, vhdDestFile, progressInfo, cancellationToken);
            _logger.LogInformation("Converted Qcow2 to VHDX: {ConvertedFile}", convertedFile);

            var resolution = await _generationResolver.ResolveAsync(convertedFile, plan, cancellationToken);
            _vmGeneration = resolution.VmGeneration;

            return new MediaPreparationResult(vhdDestFile, _vmGeneration, resolution.DetectedVirtualSizeBytes);
        }
    }
}
