using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
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

        public abstract DiskImageFormat FileType { get; }

        public abstract bool RequiresExtraction { get; }

        public virtual int VmGeneration => 2; // Default to Gen2 (UEFI/GPT)

        public virtual long DetectedVirtualSizeBytes { get; protected set; }

        /// <summary>
        /// Computes the auto-detected new drive size in GB from a virtual size in bytes.
        /// Uses max(110% of source, source + 2 GB), rounded up to the next whole GB.
        /// </summary>
        protected static int ComputeAutoDriveSizeGB(long virtualSizeBytes)
        {
            const long twoGB = 2L * 1024 * 1024 * 1024;
            double expanded = Math.Max(virtualSizeBytes * 1.10, virtualSizeBytes + twoGB);
            return (int)Math.Ceiling(expanded / (1024.0 * 1024 * 1024));
        }

        public virtual async Task<MediaPreparationResult> PrepareMediaAsync(
            string sourceFile,
            string destinationPath,
            VmDeploymentPlan plan,
            GalleryItem item,
            IProgress<CreateVMProgressInfo> progressInfo,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Checking source file: {SourceFile}", sourceFile);
            if (!File.Exists(sourceFile))
            {
                _logger.LogError("Source file not found at: {SourceFile}", sourceFile);
                throw new FileNotFoundException($"Source file not found at {sourceFile}");
            }

            string destFile = Path.Combine(destinationPath, Path.GetFileName(sourceFile));

            // When extraction happens directly into the VM disk directory, the source
            // and destination are the same file. Avoid deleting/moving ourselves.
            if (string.Equals(sourceFile, destFile, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Source file is already in the destination directory: {SourceFile}", sourceFile);
                return new MediaPreparationResult(sourceFile, VmGeneration);
            }

            if (File.Exists(destFile))
            {
                try
                {
                    File.Delete(destFile);
                    _logger.LogInformation("Deleted existing file at: {DestFile}", destFile);
                }
                catch (IOException) when (IsVhdxLocked(destFile))
                {
                    // The VHDX may be mounted (leftover from a failed UnattendInjector run)
                    // or attached to a VM. Try Dismount-VHD to release it.
                    _logger.LogWarning("Destination VHDX is locked — attempting Dismount-VHD: {DestFile}", destFile);
                    TryDismountVhdx(destFile);
                    File.Delete(destFile);
                    _logger.LogInformation("Deleted existing file after Dismount-VHD: {DestFile}", destFile);
                }
            }
            File.Move(sourceFile, destFile);
            _logger.LogInformation("Moved file to: {DestFile}", destFile);
            return new MediaPreparationResult(destFile, VmGeneration);
        }

        protected static bool IsVhdxLocked(string path)
            => path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase);

        protected void TryDismountVhdx(string vhdxPath)
        {
            try
            {
                using var ps = PowerShell.Create();
                ps.AddCommand("Import-Module").AddParameter("Name", "Hyper-V").Invoke();
                ps.Commands.Clear();
                ps.AddCommand("Dismount-VHD").AddParameter("Path", vhdxPath);
                ps.Invoke();
                if (ps.HadErrors)
                    _logger.LogDebug("Dismount-VHD reported errors (VHDX may not have been mounted): {Error}",
                        string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dismount-VHD threw (VHDX may not have been mounted)");
            }
        }
    }
}