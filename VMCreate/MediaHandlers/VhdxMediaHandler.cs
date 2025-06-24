using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Management.Automation;
using VMCreate;
using VMCreateVM;
using System;

namespace VMCreateVM.MediaHandlers
{
    public class VhdxMediaHandler : MediaHandler
    {
        private readonly IPartitionSchemeDetector _partitionSchemeDetector;
        private int _vmGeneration;

        public VhdxMediaHandler(ILogger<VhdxMediaHandler> logger, IPartitionSchemeDetector partitionSchemeDetector)
            : base(logger)
        {
            _partitionSchemeDetector = partitionSchemeDetector ?? throw new ArgumentNullException(nameof(partitionSchemeDetector));
        }

        public override bool RequiresExtraction => true;

        public override int VmGeneration => _vmGeneration;

        public override async Task PrepareMediaAsync(string sourceFile, string destinationPath, GalleryItem item, IProgress<CreateVMProgressInfo> progressInfo, CancellationToken cancellationToken)
        {
            await base.PrepareMediaAsync(sourceFile, destinationPath, item, progressInfo, cancellationToken);
            string mediaPath = Path.Combine(destinationPath, Path.GetFileName(sourceFile));
            string partitionScheme = await _partitionSchemeDetector.DetectPartitionSchemeAsync(mediaPath);
            _vmGeneration = partitionScheme == "GPT" ? 2 : 1;
            _logger.LogInformation("Detected {PartitionScheme} partition scheme, setting VM generation to {Generation}", partitionScheme, _vmGeneration);
        }

        public override async Task AttachMediaAsync(PowerShell ps, string vmName, string mediaPath, GalleryItem item, ILogger logger)
        {
            logger.LogDebug("Checking VHD destination: {MediaPath}", mediaPath);
            if (!File.Exists(mediaPath))
            {
                logger.LogError("VHD not found at: {MediaPath}", mediaPath);
                throw new FileNotFoundException($"VHD not found at {mediaPath}");
            }

            ps.Commands.Clear();
            ps.AddCommand("Add-VMHardDiskDrive")
                .AddParameter("VMName", vmName)
                .AddParameter("Path", mediaPath)
                .AddParameter("ControllerType", "SCSI");
            await Task.Run(() => ps.Invoke());
            logger.LogInformation("Attached VHD: {MediaPath}", mediaPath);

            if (ps.HadErrors)
            {
                throw new Exception($"Failed to attach VHD: {ps.Streams.Error[0]}");
            }
        }
    }
}