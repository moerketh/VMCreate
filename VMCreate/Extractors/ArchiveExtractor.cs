using System;
using System.IO;
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
                    var totalSize = archive.TotalUncompressedSize;

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

                    var entryProgress = new Progress<ProgressReport>(report =>
                    {
                        if (report.PercentComplete.HasValue)
                        {
                            // Estimate overall progress from cumulative bytes + current entry progress
                            var entryBytes = report.BytesTransferred;
                            var overall = totalSize > 0
                                ? ((double)(cumulativeBytes + entryBytes) / totalSize) * 100
                                : 0;
                            progressReportInfo.Report(new CreateVMProgressInfo
                            {
                                Phase = "Extracting Archive...",
                                URI = Path.Combine(extractPath, report.EntryPath ?? ""),
                                DownloadSpeed = -1,
                                ProgressPercentage = Convert.ToInt32(Math.Min(overall, 100))
                            });
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                    });

                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.IsDirectory)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (entry.Key == null)
                            {
                                // Single-file compressors (e.g. gzip) don't store a filename.
                                // Derive the output name from the archive path by stripping the
                                // compression extension (e.g. disk.vmdk.gz → disk.vmdk).
                                string outputName = Path.GetFileNameWithoutExtension(filePath);
                                string outputPath = Path.Combine(extractPath, outputName);
                                _logger.LogDebug("Writing keyless archive entry as {OutputPath}", outputPath);
                                using var entryStream = entry.OpenEntryStream();
                                using var fileStream = File.Create(outputPath);
                                entryStream.CopyTo(fileStream);
                            }
                            else
                            {
                                _logger.LogDebug("Writing archive entry {EntryKey} to {ExtractPath}", entry.Key, extractPath);
                                entry.WriteToDirectory(extractPath, new ExtractionOptions
                                {
                                    ExtractFullPath = true,
                                    Overwrite = true
                                });
                            }

                            cumulativeBytes += entry.Size;
                        }
                    }
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