using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;
using Microsoft.Extensions.Logging;

namespace VMCreate
{
    /// <summary>
    /// Extracts multi-file archives (e.g., .zip, .7z, .rar, .gzip, .tar) using SharpCompress's ArchiveFactory.
    /// </summary>
    /// <remarks>
    /// This class handles archive formats supported by SharpCompress's ArchiveFactory, excluding .xz files, which are processed by XzFileExtractor.
    /// The decision to split from XzFileExtractor was made to:
    /// - Isolate .xz-specific logic, as .xz files require ReaderFactory and lack progress events like CompressedBytesRead, unlike ArchiveFactory-supported formats.
    /// - Address format detection issues with .xz files, which caused InvalidOperationException in ArchiveFactory.
    /// </remarks>
    public class ArchiveExtractor : IExtractor
    {
        private readonly ILogger<ArchiveExtractor> _logger;

        /// <summary>
        /// Synchronous IProgress<T> wrapper for inline callback invocation.
        /// Unlike Progress<T>, which posts to the synchronization context asynchronously,
        /// this invokes the handler immediately on the calling thread. This ensures:
        /// - Progress reports arrive synchronously during extraction (critical for tests)
        /// </summary>
        private sealed class ImmediateProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public ImmediateProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }

        /// <summary>
        /// Stream wrapper that throws OperationCanceledException when the token is cancelled.
        /// This lets cancellation abort entry.WriteTo() from the destination side without
        /// throwing out of an IProgress<T>.Report callback, which can surface as an unhandled
        /// exception when SharpCompress invokes it from a background thread.
        /// </summary>
        private sealed class CancellationTokenStream : Stream
        {
            private readonly Stream _inner;
            private readonly CancellationToken _cancellationToken;

            public CancellationTokenStream(Stream inner, CancellationToken cancellationToken)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _cancellationToken = cancellationToken;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush()
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _inner.Flush();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                return _inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                return _inner.Seek(offset, origin);
            }

            public override void SetLength(long value)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _inner.SetLength(value);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _inner.Write(buffer, offset, count);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        public ArchiveExtractor(ILogger<ArchiveExtractor> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Extract(string filePath, string extractPath, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo)
        {
            try
            {
                _logger.LogInformation("Starting archive extraction of {FilePath} to {ExtractPath}", filePath, extractPath);

                if (!File.Exists(filePath))
                {
                    _logger.LogError("File {FilePath} does not exist", filePath);
                    throw new FileNotFoundException("Input file does not exist", filePath);
                }

                SetupExtractDirectory(extractPath, _logger);

                using (var archive = ArchiveFactory.OpenArchive(filePath))
                {
                    long totalSize = archive.TotalUncompressedSize;

                    // Pre-flight: check available disk space before extracting
                    if (totalSize > 0)
                    {
                        var driveInfo = new DriveInfo(Path.GetPathRoot(extractPath));
                        if (driveInfo.AvailableFreeSpace < totalSize)
                        {
                            string needed = FormatBytes(totalSize);
                            string available = FormatBytes(driveInfo.AvailableFreeSpace);
                            string msg = $"Not enough disk space on {driveInfo.Name} to extract the archive. " +
                                         $"Need {needed}, only {available} available. Free up space and try again.";
                            _logger.LogError(msg);
                            throw new IOException(msg);
                        }
                    }

                    long cumulativeBytes = 0;
                    int lastReportedPercentage = -1;
                    long lastReportTimeTicks = 0;
                    const long ThrottleIntervalTicks = TimeSpan.TicksPerMillisecond * 200;

                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (entry.IsDirectory)
                        {
                            // Create directory entries so nested paths exist before file extraction.
                            string dirPath = entry.Key != null
                                ? Path.Combine(extractPath, NormalizePath(entry.Key))
                                : extractPath;
                            Directory.CreateDirectory(dirPath);
                            continue;
                        }

                        string entryName = entry.Key;
                        string destinationPath;

                        if (entryName == null)
                        {
                            // Single-file compressors (e.g. gzip) don't store a filename.
                            // Derive the output name from the archive path by stripping the
                            // compression extension (e.g. disk.vmdk.gz → disk.vmdk).
                            string outputName = Path.GetFileNameWithoutExtension(filePath);
                            destinationPath = Path.Combine(extractPath, outputName);
                        }
                        else
                        {
                            destinationPath = Path.Combine(extractPath, NormalizePath(entryName));
                        }

                        // Ensure parent directory exists (in case directory entries were missing)
                        string parentDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }

                        _logger.LogDebug("Writing archive entry {EntryKey} to {DestinationPath}", entryName ?? "(keyless)", destinationPath);

                        using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        using (var cancellationStream = new CancellationTokenStream(fileStream, cancellationToken))
                        {
                            var entryProgress = new ImmediateProgress<ProgressReport>(report =>
                            {
                                if (report.PercentComplete.HasValue)
                                {
                                    long entryBytes = report.BytesTransferred;
                                    double overall = totalSize > 0
                                        ? ((double)(cumulativeBytes + entryBytes) / totalSize) * 100.0
                                        : 0.0;
                                    int overallPct = Convert.ToInt32(Math.Min(overall, 100.0));

                                    long now = Stopwatch.GetTimestamp();
                                    bool shouldReport = overallPct != lastReportedPercentage
                                        || (now - lastReportTimeTicks) >= ThrottleIntervalTicks;

                                    if (shouldReport)
                                    {
                                        lastReportedPercentage = overallPct;
                                        lastReportTimeTicks = now;

                                        progressReportInfo.Report(new CreateVMProgressInfo
                                        {
                                            Phase = VmDeploymentPhase.Extract,
                                            URI = destinationPath,
                                            DownloadSpeed = -1,
                                            ProgressPercentage = overallPct
                                        });
                                    }
                                }
                            });

                            entry.WriteTo(cancellationStream, entryProgress);
                        }

                        // Preserve file time if available
                        if (entry.LastModifiedTime.HasValue)
                        {
                            try
                            {
                                File.SetLastWriteTime(destinationPath, entry.LastModifiedTime.Value);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to set last write time for {DestinationPath}", destinationPath);
                            }
                        }

                        cumulativeBytes += entry.Size;
                    }

                    // Report 100% on completion
                    progressReportInfo.Report(new CreateVMProgressInfo
                    {
                        Phase = VmDeploymentPhase.Extract,
                        URI = extractPath,
                        DownloadSpeed = -1,
                        ProgressPercentage = 100
                    });
                }

                _logger.LogInformation("Successfully extracted archive {FilePath} to {ExtractPath}", filePath, extractPath);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Cannot determine compressed stream type"))
            {
                _logger.LogError(ex, "Failed to determine compressed stream type for {FilePath}. Supported formats: Zip, Rar, 7Zip, GZip, Tar. Ensure file format is valid.", filePath);
                throw;
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070070))
            {
                string drive = Path.GetPathRoot(extractPath) ?? extractPath;
                _logger.LogError(ex, "Not enough disk space to extract {FilePath} to {ExtractPath}", filePath, extractPath);
                throw new IOException($"Not enough disk space on {drive} to extract the archive. Free up space and try again.", ex);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Archive extraction of {FilePath} was cancelled", filePath);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract archive {FilePath} to {ExtractPath}", filePath, extractPath);
                throw;
            }
        }

        private static void SetupExtractDirectory(string extractPath, ILogger logger)
        {
            if (Directory.Exists(extractPath))
            {
                logger.LogDebug("Deleting existing directory {ExtractPath}", extractPath);
                Directory.Delete(extractPath, true);
            }
            logger.LogDebug("Creating directory {ExtractPath}", extractPath);
            Directory.CreateDirectory(extractPath);
        }

        private static string NormalizePath(string path)
        {
            // Replace forward slashes with OS-specific separators and trim trailing slashes.
            return path.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int i = 0;
            while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
            return $"{value:F1} {units[i]}";
        }
    }
}
