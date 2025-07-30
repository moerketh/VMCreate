using System.Threading.Tasks;
using System;
using System.IO;
using System.Linq;
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

                        bool isMbrBufferZero = buffer.Take(SectorSize).All(b => b == 0);
                        bool isGptBufferZero = buffer.Skip(SectorSize).Take(SectorSize).All(b => b == 0);

                        if (!isMbrBufferZero)
                        {
                            string mbrBufferHex = BitConverter.ToString(buffer, 0, Math.Min(16, SectorSize)).Replace("-", " ");
                        }

                        if (!isGptBufferZero)
                        {
                            string gptBufferHex = BitConverter.ToString(buffer, SectorSize, Math.Min(16, SectorSize)).Replace("-", " ");
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
                                string partitionTableHex = BitConverter.ToString(buffer, 0x1BE, 64).Replace("-", " ");
                                string signatureHex = BitConverter.ToString(buffer, 0x1FE, 2).Replace("-", " ");
                                _logger.LogDebug("Protective MBR (GPT) found at offset {Offset}, partition table: {TableHex}, signature: {SignatureHex}",
                                    offset, partitionTableHex, signatureHex);
                            }
                            else
                            {
                                mbrFound = true;
                                mbrOffsetFound = offset;
                                string partitionTableHex = BitConverter.ToString(buffer, 0x1BE, 64).Replace("-", " ");
                                string signatureHex = BitConverter.ToString(buffer, 0x1FE, 2).Replace("-", " ");
                                _logger.LogDebug("True MBR signature found at offset {Offset}, partition count: {Count}, partition table: {TableHex}, signature: {SignatureHex}",
                                    offset, partitionCount, partitionTableHex, signatureHex);
                            }
                        }

                        string gptSignature = System.Text.Encoding.ASCII.GetString(buffer, SectorSize, 8);
                        if (gptSignature == "EFI PART")
                        {
                            string gptSignatureHex = BitConverter.ToString(buffer, SectorSize, 8).Replace("-", " ");
                            _logger.LogInformation("Detected GPT partition scheme at offset {Offset}, GPT signature: {SignatureHex}",
                                offset + SectorSize, gptSignatureHex);
                            return "GPT";
                        }

                        offset += SectorSize;
                    }

                    if (mbrFound)
                    {
                        _logger.LogInformation("Detected MBR partition scheme at offset {Offset}", mbrOffsetFound);
                        return "MBR";
                    }

                    _logger.LogWarning("Could not determine partition scheme, defaulting to GPT");
                    return "GPT";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting partition scheme: {Message}", ex.Message);
                throw;
            }
        }
    }
}