using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System;

namespace VMCreate.MediaHandlers
{
    public class VhdxMediaHandler : MediaHandler
    {
        private readonly IPartitionSchemeDetector _partitionSchemeDetector;
        private readonly IDiskConverter _diskConverter;
        private int _vmGeneration;

        public VhdxMediaHandler(ILogger<VhdxMediaHandler> logger, IPartitionSchemeDetector partitionSchemeDetector, IDiskConverter diskConverter)
            : base(logger)
        {
            _partitionSchemeDetector = partitionSchemeDetector ?? throw new ArgumentNullException(nameof(partitionSchemeDetector));
            _diskConverter = diskConverter ?? throw new ArgumentNullException(nameof(diskConverter));
        }

        public override bool RequiresExtraction => true;

        public override int VmGeneration => _vmGeneration;

        public override async Task<string> PrepareMediaAsync(string sourceFile, string destinationPath, VmSettings vmSettings, GalleryItem item, IProgress<CreateVMProgressInfo> progressInfo, CancellationToken cancellationToken)
        {
            string vhdDestFile = await base.PrepareMediaAsync(sourceFile, destinationPath, vmSettings, item, progressInfo, cancellationToken);
            string mediaPath = Path.Combine(destinationPath, Path.GetFileName(sourceFile));
            string partitionScheme = await _partitionSchemeDetector.DetectPartitionSchemeAsync(mediaPath);
            _vmGeneration = partitionScheme == "GPT" ? 2 : 1;
            _logger.LogInformation("Detected {PartitionScheme} partition scheme, setting VM generation to {Generation}", partitionScheme, _vmGeneration);

            if (_vmGeneration == 1)
            {
                long virtualSizeBytes = await _diskConverter.GetVirtualSizeAsync(mediaPath, cancellationToken);
                DetectedVirtualSizeBytes = virtualSizeBytes;

                if (vmSettings.AutoDetectDiskSize)
                {
                    int autoGB = ComputeAutoDriveSizeGB(virtualSizeBytes);
                    vmSettings.NewDriveSizeInGB = autoGB;
                    _logger.LogInformation("Auto-detected disk size: source={SourceGB:F1} GB, target={TargetGB} GB",
                        virtualSizeBytes / (1024.0 * 1024 * 1024), autoGB);
                }
                else
                {
                    long newDriveSizeBytes = vmSettings.NewDriveSizeInGB * 1024L * 1024L * 1024L;
                    if (newDriveSizeBytes < virtualSizeBytes)
                    {
                        long minimumGB = (long)Math.Ceiling((double)virtualSizeBytes / (1024 * 1024 * 1024));
                        throw new InvalidOperationException(
                            $"New drive size ({vmSettings.NewDriveSizeInGB} GB) is too small for the source disk ({minimumGB} GB). " +
                            $"The new drive must be at least {minimumGB} GB for MBR-to-GPT cloning.");
                    }
                }
            }

            return vhdDestFile;
        }
    }
}