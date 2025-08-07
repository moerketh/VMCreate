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
        private int _vmGeneration;

        public VhdxMediaHandler(ILogger<VhdxMediaHandler> logger, IPartitionSchemeDetector partitionSchemeDetector)
            : base(logger)
        {
            _partitionSchemeDetector = partitionSchemeDetector ?? throw new ArgumentNullException(nameof(partitionSchemeDetector));
        }

        public override bool RequiresExtraction => true;

        public override int VmGeneration => _vmGeneration;

        public override async Task<string> PrepareMediaAsync(string sourceFile, string destinationPath, GalleryItem item, IProgress<CreateVMProgressInfo> progressInfo, CancellationToken cancellationToken)
        {
            string vhdDestFile = await base.PrepareMediaAsync(sourceFile, destinationPath, item, progressInfo, cancellationToken);
            string mediaPath = Path.Combine(destinationPath, Path.GetFileName(sourceFile));
            string partitionScheme = await _partitionSchemeDetector.DetectPartitionSchemeAsync(mediaPath);
            _vmGeneration = partitionScheme == "GPT" ? 2 : 1;
            _logger.LogInformation("Detected {PartitionScheme} partition scheme, setting VM generation to {Generation}", partitionScheme, _vmGeneration);
            return vhdDestFile;
        }
    }
}