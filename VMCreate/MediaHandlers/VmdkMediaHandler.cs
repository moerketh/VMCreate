using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Management.Automation;
using VMCreate;
using System;

namespace VMCreateVM.MediaHandlers
{
    public class VmdkMediaHandler : MediaHandler
    {
        private readonly DiskConverter _diskConverter;
        private readonly IPartitionSchemeDetector _partitionSchemeDetector;
        private int _vmGeneration;
        private string _vhdDestFile = null;

        public VmdkMediaHandler(ILogger<VmdkMediaHandler> logger, DiskConverter diskConverter, IPartitionSchemeDetector partitionSchemeDetector)
            : base(logger)
        {
            _diskConverter = diskConverter ?? throw new ArgumentNullException(nameof(diskConverter));
            _partitionSchemeDetector = partitionSchemeDetector ?? throw new ArgumentNullException(nameof(partitionSchemeDetector));
        }

        public override bool RequiresExtraction => true;

        public override int VmGeneration => _vmGeneration;

        public override async Task PrepareMediaAsync(string sourceFile, string destinationPath, GalleryItem item, IProgress<CreateVMProgressInfo> progressInfo, CancellationToken cancellationToken)
        {
            _vhdDestFile = Path.Combine(destinationPath, Path.GetFileNameWithoutExtension(item.ArchiveRelativePath) + ".vhdx");
            _logger.LogInformation("Converting VMDK to VHDX: {VhdDestFile}", _vhdDestFile);
            string convertedFile = await _diskConverter.ConvertToVhdxAsync(sourceFile, _vhdDestFile, progressInfo);
            _logger.LogInformation("Converted VMDK to VHDX: {ConvertedFile}", convertedFile);

            string partitionScheme = await _partitionSchemeDetector.DetectPartitionSchemeAsync(convertedFile);
            _vmGeneration = partitionScheme == "GPT" ? 2 : 1;
            _logger.LogInformation("Detected {PartitionScheme} partition scheme, setting VM generation to {Generation}", partitionScheme, _vmGeneration);
        }

        public override async Task AttachMediaAsync(PowerShell ps, string vmName, string mediaPath, GalleryItem item, ILogger logger)
        {
            logger.LogDebug("Checking VHDX destination: {_vhdDestFile}", _vhdDestFile);
            if (!File.Exists(_vhdDestFile))
            {
                logger.LogError("VHDX not found at: {_vhdDestFile}", _vhdDestFile);
                throw new FileNotFoundException($"VHDX not found at {_vhdDestFile}");
            }

            ps.Commands.Clear();
            ps.AddCommand("Add-VMHardDiskDrive")
                .AddParameter("VMName", vmName)
                .AddParameter("Path", _vhdDestFile)
                .AddParameter("ControllerType", "SCSI");
            await Task.Run(() => ps.Invoke());
            logger.LogInformation("Attached VHDX: {_vhdDestFile}", _vhdDestFile);

            if (ps.HadErrors)
            {
                throw new Exception($"Failed to attach VHDX: {ps.Streams.Error[0]}");
            }
        }
    }
}