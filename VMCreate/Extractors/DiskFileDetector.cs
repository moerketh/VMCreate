using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace VMCreate
{
    /// <summary>
    /// Scans a directory for a supported virtual-disk file after archive extraction.
    /// Handles nested archives (e.g. OVA inside a ZIP) by re-extracting one level deep.
    /// </summary>
    public class DiskFileDetector
    {
        // Ordered by preference — VMDK and QCOW2 first (most common OVA/archive contents),
        // then native Hyper-V formats, then ISO.
        private static readonly string[] DiskExtensions =
            { ".vmdk", ".qcow2", ".vhdx", ".vhd", ".iso" };

        private static readonly string[] ArchiveExtensions =
            { ".ova", ".zip", ".7z", ".tar", ".gz", ".xz", ".rar" };

        private readonly IExtractor _extractor;
        private readonly ILogger<DiskFileDetector> _logger;

        public DiskFileDetector(IExtractor extractor, ILogger<DiskFileDetector> logger)
        {
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Finds a supported disk file in <paramref name="directory"/>.
        /// If the directory contains no disk files but does contain a nested archive
        /// (e.g. an OVA extracted from a ZIP), extracts it and searches again.
        /// </summary>
        /// <returns>Full path to the best-matching disk file.</returns>
        /// <exception cref="FileNotFoundException">No supported disk file found.</exception>
        public string FindDiskFile(string directory, CancellationToken cancellationToken = default,
            IProgress<CreateVMProgressInfo> progress = null)
        {
            string disk = ScanForDisk(directory);
            if (disk != null)
                return disk;

            // No disk found — look for a nested archive (e.g. OVA inside a ZIP)
            string nestedArchive = FindNestedArchive(directory);
            if (nestedArchive != null)
            {
                _logger.LogInformation("Found nested archive {Archive}, extracting", nestedArchive);

                // Move the archive out of the extract directory first — the extractor
                // deletes the target directory before writing, which would destroy the
                // source file if it still lived inside that same directory.
                string tempArchive = Path.Combine(Path.GetTempPath(), Path.GetFileName(nestedArchive));
                File.Move(nestedArchive, tempArchive, true);

                try
                {
                    _extractor.Extract(tempArchive, directory, cancellationToken,
                        progress ?? new Progress<CreateVMProgressInfo>());
                }
                finally
                {
                    try { File.Delete(tempArchive); } catch { /* best effort */ }
                }

                disk = ScanForDisk(directory);
                if (disk != null)
                    return disk;
            }

            throw new FileNotFoundException(
                $"No supported disk file found in {directory}. " +
                $"Expected one of: {string.Join(", ", DiskExtensions)}");
        }

        /// <summary>
        /// Determines the media type from an actual file path (by extension).
        /// </summary>
        public static string DetectFileType(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return "Unknown";

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".vmdk" => "VMDK",
                ".qcow2" => "QCOW2",
                ".vhdx" => "VHDX",
                ".vhd" => "VHD",
                ".iso" => "ISO",
                _ => "Other"
            };
        }

        private string ScanForDisk(string directory)
        {
            if (!Directory.Exists(directory))
                return null;

            // Search top-level and one level deep (OVAs may have a subdirectory)
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);

            // Group candidates by disk-extension priority
            foreach (var ext in DiskExtensions)
            {
                var candidates = files
                    .Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (candidates.Count == 0)
                    continue;

                // Prefer the largest file (main disk vs. small manifest files)
                var best = candidates.OrderByDescending(f => new FileInfo(f).Length).First();
                _logger.LogInformation("Auto-detected disk file {DiskFile}", best);
                return best;
            }

            return null;
        }

        private string FindNestedArchive(string directory)
        {
            if (!Directory.Exists(directory))
                return null;

            foreach (var ext in ArchiveExtensions)
            {
                var candidate = Directory.EnumerateFiles(directory, $"*{ext}", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (candidate != null)
                    return candidate;
            }

            return null;
        }
    }
}
