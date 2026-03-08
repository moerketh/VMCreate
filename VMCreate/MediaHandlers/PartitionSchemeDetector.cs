using System.Threading.Tasks;
using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace VMCreate
{
    public interface IPartitionSchemeDetector
    {
        Task<string> DetectPartitionSchemeAsync(string diskPath);
    }
}

namespace VMCreate
{
    public class PartitionSchemeDetector : IPartitionSchemeDetector
    {
        private readonly ILogger<PartitionSchemeDetector> _logger;
        private const long ScanStartOffset = 0x10000; // 64KB, minimum for VHDX headers
        private const long ScanEndOffset = 0xA00000; // 10MB, reasonable limit for data region
        private const int SectorSize = 512; // Standard disk sector size

        public PartitionSchemeDetector(ILogger<PartitionSchemeDetector> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<string> DetectPartitionSchemeAsync(string diskPath)
        {
            _logger.LogInformation("Detecting partition scheme for disk: {DiskPath}", diskPath);
            return await DetectViaRawScanAsync(diskPath);
        }

        private async Task<string> DetectViaRawScanAsync(string diskPath)
        {
            _logger.LogInformation("Falling back to raw byte scan for: {DiskPath}", diskPath);
            try
            {
                using (FileStream stream = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bool mbrFound = false;
                    long mbrOffsetFound = 0;
                    long offset = ScanStartOffset;
                    byte[] buffer = new byte[SectorSize * 2]; // 512 for MBR + 512 for GPT

                    while (offset <= ScanEndOffset)
                    {
                        stream.Seek(offset, SeekOrigin.Begin);
                        int bytesRead = await stream.ReadAsync(buffer, 0, SectorSize * 2);
                        if (bytesRead < SectorSize * 2)
                        {
                            _logger.LogWarning("Reached end of file or insufficient data at offset {Offset}: {DiskPath}", offset, diskPath);
                            break;
                        }

                        bool isMbr = buffer[0x1FE] == 0x55 && buffer[0x1FF] == 0xAA;
                        if (isMbr)
                        {
                            bool isProtectiveMbr = false;
                            int partitionCount = 0;
                            for (int i = 0x1BE; i < 0x1FE; i += 16)
                            {
                                byte partitionType = buffer[i + 4];
                                if (partitionType != 0)
                                {
                                    partitionCount++;
                                    if (partitionType == 0xEE)
                                    {
                                        isProtectiveMbr = true;
                                    }
                                }
                            }

                            if (isProtectiveMbr && partitionCount == 1)
                            {
                                _logger.LogDebug("Protective MBR (GPT) found at offset {Offset}", offset);
                            }
                            else
                            {
                                mbrFound = true;
                                mbrOffsetFound = offset;
                                _logger.LogDebug("True MBR signature found at offset {Offset}, partition count: {Count}",
                                    offset, partitionCount);
                            }
                        }

                        string gptSignature = System.Text.Encoding.ASCII.GetString(buffer, SectorSize, 8);
                        if (gptSignature == "EFI PART")
                        {
                            _logger.LogInformation("Detected GPT partition scheme at offset {Offset}",
                                offset + SectorSize);
                            return "GPT";
                        }

                        offset += SectorSize;
                    }

                    if (mbrFound)
                    {
                        _logger.LogInformation("Detected MBR partition scheme at offset {Offset}", mbrOffsetFound);
                        return "MBR";
                    }

                    // Default to MBR: it's safer to trigger an unnecessary MBR→GPT conversion
                    // than to skip a needed one and produce an unbootable VM
                    _logger.LogWarning("Could not determine partition scheme via raw scan, defaulting to MBR");
                    return "MBR";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during raw scan detection: {Message}", ex.Message);
                // Default to MBR on error for safety
                _logger.LogWarning("Defaulting to MBR after raw scan error");
                return "MBR";
            }
        }
    }
}