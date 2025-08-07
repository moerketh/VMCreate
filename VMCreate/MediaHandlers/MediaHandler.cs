using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VMCreate.MediaHandlers
{
    public abstract class MediaHandler : IMediaHandler
    {
        protected readonly ILogger _logger;

        protected MediaHandler(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public abstract bool RequiresExtraction { get; }

        public virtual int VmGeneration => 2; // Default to Gen2 (UEFI/GPT)

        public virtual async Task<string> PrepareMediaAsync(string sourceFile, string destinationPath, GalleryItem item, IProgress<CreateVMProgressInfo> progressInfo, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Checking source file: {SourceFile}", sourceFile);
            if (!File.Exists(sourceFile))
            {
                _logger.LogError("Source file not found at: {SourceFile}", sourceFile);
                throw new FileNotFoundException($"Source file not found at {sourceFile}");
            }

            string destFile = Path.Combine(destinationPath, Path.GetFileName(sourceFile));
            if (File.Exists(destFile))
            {
                File.Delete(destFile);
                _logger.LogInformation("Deleted existing file at: {DestFile}", destFile);
            }
            File.Move(sourceFile, destFile);
            _logger.LogInformation("Moved file to: {DestFile}", destFile);
            return destFile;
        }
    }
}