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
        internal const long ScanStartOffset = 0x10000; // 64KB, minimum for VHDX headers
        private const long ScanEndOffset = 0xA00000; // 10MB, reasonable limit for data region
        internal const int SectorSize = 512; // Standard disk sector size
        private const int DefaultGptEntrySize = 128;
        private const int MaxGptEntriesToScan = 128; // GPT standard max

        // GPT header field offsets (relative to start of GPT header sector)
        private const int GptRevisionOffset = 8;     // bytes 8-11: uint32
        private const int GptHeaderSizeOffset = 12;   // bytes 12-15: uint32
        private const int GptMyLbaOffset = 24;         // bytes 24-31: uint64
        private const int GptEntryStartLbaOffset = 72; // bytes 72-79: uint64
        private const int GptEntryCountOffset = 80;    // bytes 80-83: uint32
        private const int GptEntrySizeOffset = 84;     // bytes 84-87: uint32

        private const uint GptRevision1_0 = 0x00010000;
        private const uint GptStandardHeaderSize = 92;

        // EFI System Partition GUID: C12A7328-F81F-11D2-BA4B-00A0C93EC93B (mixed-endian)
        private static readonly byte[] EspTypeGuid =
            { 0x28, 0x73, 0x2A, 0xC1, 0x1F, 0xF8, 0xD2, 0x11, 0xBA, 0x4B, 0x00, 0xA0, 0xC9, 0x3E, 0xC9, 0x3B };

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
                            // Validate key GPT header fields before trusting this match.
                            // This rejects false positives from VHDX metadata, backup GPT headers, etc.
                            uint revision = BitConverter.ToUInt32(buffer, SectorSize + GptRevisionOffset);
                            uint headerSize = BitConverter.ToUInt32(buffer, SectorSize + GptHeaderSizeOffset);
                            ulong myLba = BitConverter.ToUInt64(buffer, SectorSize + GptMyLbaOffset);

                            if (revision != GptRevision1_0 || headerSize < GptStandardHeaderSize || myLba != 1)
                            {
                                _logger.LogDebug(
                                    "Skipping invalid GPT header at offset {Offset}: Revision=0x{Revision:X8}, HeaderSize={HeaderSize}, MyLBA={MyLBA}",
                                    offset + SectorSize, revision, headerSize, myLba);
                                offset += SectorSize;
                                continue;
                            }

                            _logger.LogInformation("Detected GPT partition scheme at offset {Offset}",
                                offset + SectorSize);

                            // Read partition entry location from the header instead of assuming
                            // entries immediately follow the header. In a VHDX container file
                            // the bytes at gptHeader+512 may be container metadata, not entries.
                            bool hasEsp = await HasEfiSystemPartitionAsync(stream, offset, buffer);
                            if (hasEsp)
                            {
                                return "GPT";
                            }

                            _logger.LogWarning("GPT disk has no EFI System Partition — treating as BIOS-only (will clone to UEFI layout)");
                            return "GPT_BIOS";
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

        /// <summary>
        /// Reads GPT partition entries and checks whether any entry has the EFI
        /// System Partition type GUID. Entry location, count, and size are read
        /// from the GPT header (bytes 72-87) rather than hardcoded.
        /// </summary>
        /// <param name="stream">Open file stream positioned anywhere.</param>
        /// <param name="mbrFileOffset">File offset of the MBR sector (virtual disk base).</param>
        /// <param name="scanBuffer">
        /// The 1024-byte buffer from the scan loop. GPT header occupies bytes [512..1023].
        /// </param>
        private async Task<bool> HasEfiSystemPartitionAsync(FileStream stream, long mbrFileOffset, byte[] scanBuffer)
        {
            try
            {
                // Parse entry array location from the GPT header (already in scanBuffer)
                ulong entryStartLba = BitConverter.ToUInt64(scanBuffer, SectorSize + GptEntryStartLbaOffset);
                uint entryCount = BitConverter.ToUInt32(scanBuffer, SectorSize + GptEntryCountOffset);
                uint entrySize = BitConverter.ToUInt32(scanBuffer, SectorSize + GptEntrySizeOffset);

                // Sanity-check values to avoid reading garbage
                if (entrySize < 128 || entrySize > 4096 || entryCount == 0)
                {
                    _logger.LogDebug("GPT header has suspicious entry values: count={Count}, size={Size}", entryCount, entrySize);
                    return false;
                }

                int entriesToCheck = (int)Math.Min(entryCount, MaxGptEntriesToScan);
                int bytesToRead = entriesToCheck * (int)entrySize;

                // Calculate file offset: MBR is LBA 0, so entries are at mbrOffset + entryStartLba * 512
                long entriesFileOffset = mbrFileOffset + (long)entryStartLba * SectorSize;

                byte[] entries = new byte[bytesToRead];
                stream.Seek(entriesFileOffset, SeekOrigin.Begin);
                int bytesRead = await stream.ReadAsync(entries, 0, bytesToRead);

                int actualEntries = Math.Min(entriesToCheck, bytesRead / (int)entrySize);
                for (int i = 0; i < actualEntries; i++)
                {
                    int entryBase = i * (int)entrySize;

                    // Check if entry is empty (all-zero type GUID)
                    bool isEmpty = true;
                    for (int b = 0; b < 16; b++)
                    {
                        if (entries[entryBase + b] != 0) { isEmpty = false; break; }
                    }
                    if (isEmpty) break;

                    // Compare type GUID with ESP GUID
                    bool isEsp = true;
                    for (int b = 0; b < 16; b++)
                    {
                        if (entries[entryBase + b] != EspTypeGuid[b]) { isEsp = false; break; }
                    }
                    if (isEsp)
                    {
                        _logger.LogInformation("Found EFI System Partition at entry {Index}", i);
                        return true;
                    }
                }

                _logger.LogDebug("Scanned {Count} GPT entries, no ESP found", actualEntries);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read GPT partition entries, assuming ESP is present");
                return true; // Conservative: assume UEFI-capable on read failure
            }
        }
    }
}