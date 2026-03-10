using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VMCreate.MediaHandlers
{
    /// <summary>
    /// Handles ISO media files. ISOs are attached as a DVD drive and booted
    /// directly — no disk conversion or extraction is needed. A fresh empty
    /// VHDX is created as the boot/install target disk.
    /// </summary>
    public class IsoMediaHandler : MediaHandler
    {
        public IsoMediaHandler(ILogger<IsoMediaHandler> logger) : base(logger)
        {
        }

        public override bool RequiresExtraction => false;

        /// <summary>ISO installers always target Gen 2 (UEFI).</summary>
        public override int VmGeneration => 2;

        /// <summary>
        /// Returns true to signal that the media is an ISO image that should
        /// be attached as a DVD rather than as a hard drive.
        /// </summary>
        public bool IsIsoMedia => true;

        public override Task<string> PrepareMediaAsync(
            string sourceFile,
            string destinationPath,
            VmSettings vmSettings,
            GalleryItem item,
            IProgress<CreateVMProgressInfo> progressInfo,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Checking source ISO: {SourceFile}", sourceFile);
            if (!File.Exists(sourceFile))
            {
                _logger.LogError("ISO file not found at: {SourceFile}", sourceFile);
                throw new FileNotFoundException($"ISO file not found at {sourceFile}");
            }

            _logger.LogInformation("ISO media ready at: {SourceFile}", sourceFile);

            // Return the ISO path as-is — it will be attached as a DVD drive.
            return Task.FromResult(sourceFile);
        }
    }
}
