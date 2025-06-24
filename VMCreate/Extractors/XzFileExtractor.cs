using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using VMCreate;
using XZ.NET;

namespace VMCreateVM
{
    /// <summary>
    /// Extracts .xz compressed files using XZ.NET.
    /// </summary>
    /// <remarks>
    /// This class handles .xz files separately from other archive formats due to issues with SharpCompress's XZ support.
    /// Unlike multi-file archives (.zip, .7z, etc.), .xz files are single-file compression formats requiring specialized handling.
    /// The decision to split from ArchiveExtractor was made to:
    /// - Address persistent InvalidFormatException errors with SharpCompress's ReaderFactory, which failed to recognize valid .xz files despite correct magic bytes (FD 37 7A 58 5A 00).
    /// - Isolate .xz-specific logic, as SharpCompress lacked reliable progress events and format detection for certain .xz files (e.g., Parrot Security OS .vmdk.xz).
    /// - Validate .xz files using magic bytes to prevent misidentification.
    /// SharpCompress 0.40.0 was initially used with ReaderFactory, but due to consistent failures (e.g., InvalidFormatException at ReaderFactory.Open), this implementation switched to XZ.NET, a dedicated XZ decompression library.
    /// An external 'xz' command-line tool fallback was considered but removed to avoid external dependencies.
    /// XZ.NET requires liblzma.dll, which must be placed in the application's executable directory or system PATH (e.g., download from https://tukaani.org/xz/).
    /// Progress reporting is implemented using a buffer-based copy to provide incremental updates, as XZ.NET lacks native progress events.
    /// The class integrates with Serilog for detailed logging and uses dependency injection for compatibility with the application's CreateVM workflow.
    /// </remarks>
    public class XzFileExtractor : IExtractor
    {
        private readonly ILogger<XzFileExtractor> _logger;
        private const double EstimatedCompressionRatio = 1.5;

        public XzFileExtractor(ILogger<XzFileExtractor> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Extract(string filePath, string extractPath, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo)
        {
            try
            {
                _logger.LogInformation("Starting XZ extraction of {FilePath} to {ExtractPath}", filePath, extractPath);

                if (!File.Exists(filePath))
                {
                    _logger.LogError("File {FilePath} does not exist", filePath);
                    throw new FileNotFoundException("Input file does not exist", filePath);
                }

                var fileInfo = new FileInfo(filePath);
                _logger.LogDebug("File {FilePath} size: {FileSize} bytes", filePath, fileInfo.Length);

                if (!IsXzFile(filePath))
                {
                    _logger.LogError("File {FilePath} is not a valid XZ file based on magic bytes", filePath);
                    throw new InvalidOperationException("File is not a valid XZ file");
                }

                LogFileHeader(filePath);

                SetupExtractDirectory(extractPath, _logger);

                string filePart = Path.GetFileNameWithoutExtension(filePath);
                string outputPath = Path.Combine(extractPath, filePart);
                
                // Estimate uncompressed size for progress
                long estimatedUncompressedSize = (long)(fileInfo.Length * EstimatedCompressionRatio);
                _logger.LogDebug("Estimated uncompressed size for {FilePath}: {EstimatedSize} bytes (using {CompressionRatio}:1 ratio)", filePath, estimatedUncompressedSize, EstimatedCompressionRatio);

                _logger.LogInformation("Attempting extraction with XZ.NET for {FilePath}", filePath);
                using (var inputStream = File.OpenRead(filePath))
                using (var xzStream = new XZInputStream(inputStream)) // Requires liblzma.dll
                using (var outputStream = File.Create(outputPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.LogDebug("Decompressing XZ stream to {OutputPath}", outputPath);

                    byte[] buffer = new byte[8192];
                    int bytesRead;
                    long totalBytesWritten = 0;

                    while ((bytesRead = xzStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        outputStream.Write(buffer, 0, bytesRead);
                        totalBytesWritten += bytesRead;

                        // // Progress based on estimated uncompressed size
                        var progress = estimatedUncompressedSize > 0 ? (totalBytesWritten / (double)estimatedUncompressedSize) * 100 : 0;
                        progressReportInfo.Report(new CreateVMProgressInfo
                        {
                            Phase = "Extracting XZ...",
                            URI = outputPath,
                            DownloadSpeed = -1,
                            ProgressPercentage = Math.Min(Convert.ToInt32(progress), 99)
                        });
                    }

                    outputStream.Flush();
                    var outputFileInfo = new FileInfo(outputPath);
                    _logger.LogDebug("Output file {OutputPath} size: {OutputSize} bytes", outputPath, outputFileInfo.Length);
                }
                progressReportInfo.Report(new CreateVMProgressInfo
                {
                    Phase = "Extracting XZ...",
                    URI = outputPath,
                    DownloadSpeed = -1,
                    ProgressPercentage = 100
                });
                _logger.LogInformation("Successfully extracted XZ file {FilePath} to {OutputPath}", filePath, outputPath);
            }
            catch (DllNotFoundException ex)
            {
                _logger.LogError(ex, "Failed to load liblzma.dll for XZ.NET. Ensure liblzma.dll is in the application's executable directory or system PATH (e.g., download from https://tukaani.org/xz/). File: {FilePath}", filePath);
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("XZ extraction of {FilePath} was cancelled", filePath);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract XZ file {FilePath} to {ExtractPath}", filePath, extractPath);
                throw;
            }
        }

        private bool IsXzFile(string filePath)
        {
            try
            {
                byte[] xzMagicBytes = new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 };
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] header = new byte[6];
                    int bytesRead = stream.Read(header, 0, 6);
                    if (bytesRead != 6)
                    {
                        _logger.LogWarning("File {FilePath} is too short to verify XZ magic bytes", filePath);
                        return false;
                    }
                    bool isXz = header.SequenceEqual(xzMagicBytes);
                    _logger.LogDebug("XZ magic bytes check for {FilePath}: {IsXz}", filePath, isXz);
                    return isXz;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to verify XZ magic bytes for {FilePath}", filePath);
                return false;
            }
        }

        private void LogFileHeader(string filePath)
        {
            try
            {
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] header = new byte[16];
                    int bytesRead = stream.Read(header, 0, 16);
                    if (bytesRead > 0)
                    {
                        string hex = BitConverter.ToString(header, 0, bytesRead).Replace("-", " ");
                        _logger.LogDebug("First {Count} bytes of {FilePath}: {Hex}", bytesRead, filePath, hex);
                    }
                    else
                    {
                        _logger.LogWarning("No bytes read from {FilePath} for header inspection", filePath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read file header for {FilePath}", filePath);
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