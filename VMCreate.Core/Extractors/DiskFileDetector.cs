using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using VMCreate.MediaHandlers;

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
            IProgress<CreateVMProgressInfo>? progress = null)
        {
            string? disk = ScanForDisk(directory);
            if (disk != null)
                return disk;

            // No disk found — look for a nested archive (e.g. OVA inside a ZIP)
            string? nestedArchive = FindNestedArchive(directory);
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

            throw new FileNotFoundException(                $"No supported disk file found in {directory}. " +
                $"Expected one of: {string.Join(", ", DiskExtensions)}");
        }

        /// <summary>
        /// Determines the media type from an actual file path (by extension).
        /// Null/empty input maps to <see cref="DiskImageFormat.Other"/>.
        /// </summary>
        public static DiskImageFormat DetectFileType(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return DiskImageFormat.Other;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            var byExtension = ext switch
            {
                ".vmdk" => DiskImageFormat.Vmdk,
                ".qcow2" => DiskImageFormat.Qcow2,
                ".vhdx" => DiskImageFormat.Vhdx,
                ".vhd" => DiskImageFormat.Vhd,
                ".iso" => DiskImageFormat.Iso,
                _ => DiskImageFormat.Other
            };

            // Ambiguous extension (e.g. Ubuntu cloud images ship qcow2 data
            // in ".img"): sniff magic bytes when a local file exists. URLs
            // and not-yet-downloaded paths keep the extension verdict.
            if (byExtension == DiskImageFormat.Other && File.Exists(filePath))
            {
                var sniffed = SniffLocalFile(filePath);
                if (sniffed.HasValue)
                    return sniffed.Value;
            }

            return byExtension;
        }

        /// <summary>
        /// Resolves the media type for a deployment, honoring a loader's
        /// explicit <see cref="GalleryItem.FileTypeOverride"/> (which wins —
        /// the loader knows the source content) and otherwise falling back to
        /// <see cref="DetectFileType"/> for the URI/path.
        /// </summary>
        public static DiskImageFormat ResolveDiskImageFormat(GalleryItem? item, string? sourceFileOrUri)
        {
            if (!string.IsNullOrEmpty(item?.FileTypeOverride)
                && Enum.TryParse<DiskImageFormat>(item.FileTypeOverride, ignoreCase: true, out var declared))
            {
                return declared;
            }

            return DetectFileType(sourceFileOrUri);
        }

        private static DiskImageFormat? SniffLocalFile(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

                Span<byte> header = stackalloc byte[4096];
                int read = 0;
                while (read < header.Length)
                {
                    int n = stream.Read(header[read..]);
                    if (n == 0) break;
                    read += n;
                }

                if (read >= 4 && header[0] == (byte)'Q' && header[1] == (byte)'F' && header[2] == (byte)'I' && header[3] == 0xFB)
                    return DiskImageFormat.Qcow2;
                if (read >= 8 && header[0] == (byte)'v' && header[1] == (byte)'h' && header[2] == (byte)'d' && header[3] == (byte)'x')
                    return DiskImageFormat.Vhdx;
                if (read >= 516 && header[512] == (byte)'K' && header[513] == (byte)'D' && header[514] == (byte)'M' && header[515] == (byte)'V')
                    return DiskImageFormat.Vmdk;
                return null;
            }
            catch
            {
                return null; // unreadable/locked — caller falls back to extension
            }
        }

        private string? ScanForDisk(string directory)
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

        private string? FindNestedArchive(string directory)
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
