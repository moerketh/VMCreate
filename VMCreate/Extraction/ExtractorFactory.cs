using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using VMCreate;

namespace VMCreateVM
{
    /// <summary>
    /// Factory for selecting the appropriate IExtractor based on file extension.
    /// </summary>
    /// <remarks>
    /// This factory was introduced to support the split of extraction logic into XzFileExtractor and ArchiveExtractor.
    /// It selects XzFileExtractor for .xz files (verified by magic bytes) and ArchiveExtractor for other supported formats.
    /// The factory ensures compatibility with CreateVM, which expects a single IExtractor instance.
    /// </remarks>
    public class ExtractorFactory : IExtractor
    {
        private readonly XzFileExtractor _xzExtractor;
        private readonly ArchiveExtractor _archiveExtractor;
        private readonly ILogger<ExtractorFactory> _logger;

        public ExtractorFactory(XzFileExtractor xzExtractor, ArchiveExtractor archiveExtractor, ILogger<ExtractorFactory> logger)
        {
            _xzExtractor = xzExtractor ?? throw new ArgumentNullException(nameof(xzExtractor));
            _archiveExtractor = archiveExtractor ?? throw new ArgumentNullException(nameof(archiveExtractor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Extract(string filePath, string extractPath, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo)
        {
            _logger.LogDebug("Selecting extractor for file {FilePath}", filePath);

            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            if (extension == ".xz" && IsXzFile(filePath))
            {
                _logger.LogInformation("Using XzFileExtractor for {FilePath}", filePath);
                _xzExtractor.Extract(filePath, extractPath, cancellationToken, progressReportInfo);
            }
            else
            {
                _logger.LogInformation("Using ArchiveExtractor for {FilePath}", filePath);
                _archiveExtractor.Extract(filePath, extractPath, cancellationToken, progressReportInfo);
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
    }
}