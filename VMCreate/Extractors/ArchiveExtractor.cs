using System;
using System.IO;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;
using Microsoft.Extensions.Logging;
using VMCreate;

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
    /// - Simplify maintenance by separating multi-file archive handling (with granular progress reporting) from single-file .xz extraction.
    /// ArchiveFactory provides robust progress reporting via events like CompressedBytesRead, enabling detailed extraction feedback.
    /// The class integrates with Serilog for logging and uses dependency injection to ensure compatibility with the application's CreateVM workflow.
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

                string filePart = "";
                double bytesRead = 0;
                double totalBytesRead = 0;

                using (var archive = ArchiveFactory.Open(filePath))
                {
                    archive.FilePartExtractionBegin += (sender, e) =>
                    {
                        filePart = e.Name;
                        totalBytesRead += bytesRead;
                        _logger.LogDebug("Extracting archive file part {FilePart}", filePart);
                    };

                    archive.CompressedBytesRead += (sender, e) =>
                    {
                        var progress = archive.TotalUncompressSize > 0
                            ? (((double)e.CompressedBytesRead + totalBytesRead) / (double)archive.TotalUncompressSize) * 100
                            : 0;
                        progressReportInfo.Report(new CreateVMProgressInfo
                        {
                            Phase = "Extracting Archive...",
                            URI = Path.Combine(extractPath, filePart),
                            DownloadSpeed = -1,
                            ProgressPercentage = Convert.ToInt32(progress)
                        });
                        bytesRead = e.CompressedBytesRead;
                    };

                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.IsDirectory)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _logger.LogDebug("Writing archive entry {EntryKey} to {ExtractPath}", entry.Key, extractPath);
                            entry.WriteToDirectory(extractPath, new ExtractionOptions
                            {
                                ExtractFullPath = true,
                                Overwrite = true
                            });
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
    }
}