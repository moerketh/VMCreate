using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VMCreate.MediaHandlers
{
    /// <summary>
    /// Default implementation of <see cref="IVmGenerationResolver"/u003e. Detects the partition
    /// scheme, maps it to a VM generation, and validates or auto-detects the new
    /// drive size for generation 1 disks.
    /// </summary>
    public sealed class VmGenerationResolver : IVmGenerationResolver
    {
        private readonly IPartitionSchemeDetector _partitionSchemeDetector;
        private readonly IDiskConverter _diskConverter;
        private readonly ILogger<VmGenerationResolver> _logger;

        public VmGenerationResolver(
            IPartitionSchemeDetector partitionSchemeDetector,
            IDiskConverter diskConverter,
            ILogger<VmGenerationResolver> logger)
        {
            _partitionSchemeDetector = partitionSchemeDetector ?? throw new ArgumentNullException(nameof(partitionSchemeDetector));
            _diskConverter = diskConverter ?? throw new ArgumentNullException(nameof(diskConverter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<VmGenerationResolution> ResolveAsync(
            string diskImagePath,
            VmDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            string partitionScheme = await _partitionSchemeDetector.DetectPartitionSchemeAsync(diskImagePath);
            int vmGeneration = partitionScheme == "GPT" ? 2 : 1;
            _logger.LogInformation(
                "Detected {PartitionScheme} partition scheme, setting VM generation to {Generation}",
                partitionScheme, vmGeneration);

            long detectedVirtualSizeBytes = 0;
            int newDriveSizeInGB = plan.NewDriveSizeInGB;
            if (vmGeneration == 1)
            {
                detectedVirtualSizeBytes = await _diskConverter.GetVirtualSizeAsync(diskImagePath, cancellationToken);
                newDriveSizeInGB = ResolveGen1DriveSize(plan, detectedVirtualSizeBytes);
            }

            return new VmGenerationResolution(vmGeneration, partitionScheme, detectedVirtualSizeBytes, newDriveSizeInGB);
        }

        private int ResolveGen1DriveSize(VmDeploymentPlan plan, long detectedVirtualSizeBytes)
        {
            if (plan.AutoDetectDiskSize)
            {
                int autoGB = ComputeAutoDriveSizeGB(detectedVirtualSizeBytes);
                _logger.LogInformation(
                    "Auto-detected disk size: source={SourceGB:F1} GB, target={TargetGB} GB",
                    detectedVirtualSizeBytes / (1024.0 * 1024 * 1024), autoGB);
                return autoGB;
            }
            else
            {
                long newDriveSizeBytes = plan.NewDriveSizeInGB * 1024L * 1024L * 1024L;
                if (newDriveSizeBytes < detectedVirtualSizeBytes)
                {
                    long minimumGB = (long)Math.Ceiling((double)detectedVirtualSizeBytes / (1024 * 1024 * 1024));
                    throw new InvalidOperationException(
                        $"New drive size ({plan.NewDriveSizeInGB} GB) is too small for the source disk ({minimumGB} GB). " +
                        $"The new drive must be at least {minimumGB} GB for MBR-to-GPT cloning.");
                }
                return plan.NewDriveSizeInGB;
            }
        }

        /// <summary>
        /// Computes the auto-detected new drive size in GB from a virtual size in bytes.
        /// Uses max(110% of source, source + 2 GB), rounded up to the next whole GB.
        /// </summary>
        private static int ComputeAutoDriveSizeGB(long virtualSizeBytes)
        {
            const long twoGB = 2L * 1024 * 1024 * 1024;
            double expanded = Math.Max(virtualSizeBytes * 1.10, virtualSizeBytes + twoGB);
            return (int)Math.Ceiling(expanded / (1024.0 * 1024 * 1024));
        }
    }
}
