using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Management.Automation;
using VMCreate;
using System;

namespace VMCreate.MediaHandlers
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

        public override async Task<string> PrepareMediaAsync(string sourceFile, string destinationPath, VmSettings vmSettings, GalleryItem item, IProgress<CreateVMProgressInfo> progressInfo, CancellationToken cancellationToken)
        {
            progressInfo.Report(new CreateVMProgressInfo { Phase = "Convert" });
            _vhdDestFile = Path.Combine(destinationPath, vmSettings.VMName + ".vhdx");
            _logger.LogInformation("Converting VMDK to VHDX: {VhdDestFile}", _vhdDestFile);
            string convertedFile = await _diskConverter.ConvertToVhdxAsync(sourceFile, _vhdDestFile, progressInfo);
            _logger.LogInformation("Converted VMDK to VHDX: {ConvertedFile}", convertedFile);

            string partitionScheme = await _partitionSchemeDetector.DetectPartitionSchemeAsync(convertedFile);
            _vmGeneration = partitionScheme == "GPT" ? 2 : 1;
            _logger.LogInformation("Detected {PartitionScheme} partition scheme, setting VM generation to {Generation}", partitionScheme, _vmGeneration);
            return _vhdDestFile;
        }
    }
}