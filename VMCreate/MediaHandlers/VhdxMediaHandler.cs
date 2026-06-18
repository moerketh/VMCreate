using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System;

namespace VMCreate.MediaHandlers
{
    public class VhdxMediaHandler : MediaHandler
    {
        private readonly IVmGenerationResolver _generationResolver;
        private readonly IDiskConverter _diskConverter;
        private int _vmGeneration;

        public VhdxMediaHandler(
            ILogger<VhdxMediaHandler> logger,
            IVmGenerationResolver generationResolver,
            IDiskConverter diskConverter)
            : base(logger)
        {
            _generationResolver = generationResolver ?? throw new ArgumentNullException(nameof(generationResolver));
            _diskConverter = diskConverter ?? throw new ArgumentNullException(nameof(diskConverter));
        }

        public override bool RequiresExtraction => true;

        public override DiskImageFormat FileType => DiskImageFormat.Vhdx;

        public override int VmGeneration => _vmGeneration;

        public override async Task<MediaPreparationResult> PrepareMediaAsync(
            string sourceFile,
            string destinationPath,
            VmDeploymentPlan plan,
            GalleryItem item,
            IProgress<CreateVMProgressInfo> progressInfo,
            CancellationToken cancellationToken)
        {
            MediaPreparationResult baseResult = await base.PrepareMediaAsync(sourceFile, destinationPath, plan, item, progressInfo, cancellationToken);
            string mediaPath = baseResult.FinalMediaPath;

            // Rename the extracted native VHDX to a VM-specific name so it is not shared
            // across deployments (e.g. WinDev2407Eval.vhdx → FLARE_VM_20260313.vhdx).
            string vmSpecificVhdx = Path.Combine(destinationPath, plan.VmName + ".vhdx");
            if (!string.Equals(mediaPath, vmSpecificVhdx, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(vmSpecificVhdx))
                {
                    try
                    {
                        File.Delete(vmSpecificVhdx);
                    }
                    catch (IOException) when (IsVhdxLocked(vmSpecificVhdx))
                    {
                        TryDismountVhdx(vmSpecificVhdx);
                        File.Delete(vmSpecificVhdx);
                    }
                }
                File.Move(mediaPath, vmSpecificVhdx);
                _logger.LogInformation("Renamed native VHDX to VM-specific path: {Path}", vmSpecificVhdx);
                mediaPath = vmSpecificVhdx;
            }

            var resolution = await _generationResolver.ResolveAsync(mediaPath, plan, cancellationToken);
            _vmGeneration = resolution.VmGeneration;

            return new MediaPreparationResult(mediaPath, _vmGeneration, resolution.DetectedVirtualSizeBytes);
        }
    }
}
