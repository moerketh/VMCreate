using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace VMCreate.Tests
{
    [TestClass]
    public class PartitionSchemeDetectorTests
    {
        private PartitionSchemeDetector _detector;
        private string _tempDir;

        // Well-known GPT type GUIDs in mixed-endian on-disk byte order
        private static readonly byte[] EspGuid =
            { 0x28, 0x73, 0x2A, 0xC1, 0x1F, 0xF8, 0xD2, 0x11, 0xBA, 0x4B, 0x00, 0xA0, 0xC9, 0x3E, 0xC9, 0x3B };
        private static readonly byte[] BiosBootGuid =
            { 0x48, 0x61, 0x68, 0x21, 0x49, 0x64, 0x6E, 0x6F, 0x74, 0x4E, 0x65, 0x65, 0x64, 0x45, 0x46, 0x49 };
        private static readonly byte[] LinuxFsGuid =
            { 0xAF, 0x3D, 0xC6, 0x0F, 0x83, 0x84, 0x72, 0x47, 0x8E, 0x79, 0x3D, 0x69, 0xD8, 0x47, 0x7D, 0xE4 };
        private static readonly byte[] MsBasicDataGuid =
            { 0xA2, 0xA0, 0xD0, 0xEB, 0xE5, 0xB9, 0x33, 0x44, 0x87, 0xC0, 0x68, 0xB6, 0xB7, 0x26, 0x99, 0xC7 };
        private static readonly byte[] MsReservedGuid =
            { 0x16, 0xE3, 0xC9, 0xE3, 0x5C, 0x0B, 0xB8, 0x4D, 0x81, 0x7D, 0xF9, 0x2D, 0xF0, 0x02, 0x15, 0xAE };
        private static readonly byte[] LinuxSwapGuid =
            { 0x6D, 0xFD, 0x57, 0x06, 0xAB, 0xA4, 0xC4, 0x43, 0x84, 0xE5, 0x09, 0x33, 0xC8, 0x4B, 0x4F, 0x4F };

        [TestInitialize]
        public void Setup()
        {
            _detector = new PartitionSchemeDetector(
                new Mock<ILogger<PartitionSchemeDetector>>().Object);
            _tempDir = Path.Combine(Path.GetTempPath(), "PartDetectorTest_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Basic detection (original tests, updated to use valid headers)
        // ══════════════════════════════════════════════════════════════════

        [TestMethod]
        public async Task Detect_GptWithEsp_ReturnsGpt()
        {
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid, LinuxFsGuid });

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        [TestMethod]
        public async Task Detect_GptWithBiosBootOnly_ReturnsGptBios()
        {
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { BiosBootGuid, LinuxFsGuid });

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT_BIOS", result);
        }

        [TestMethod]
        public async Task Detect_Mbr_ReturnsMbr()
        {
            string path = BuildDisk(protectiveMbr: false, gptHeader: false,
                mbrPartitionType: 0x83); // Linux native

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        [TestMethod]
        public async Task Detect_EmptyDisk_DefaultsToMbr()
        {
            string path = Path.Combine(_tempDir, "empty.vhdx");
            byte[] data = new byte[0xB00000]; // 11MB of zeros
            await File.WriteAllBytesAsync(path, data);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        [TestMethod]
        public async Task Detect_GptWithEspAsSecondPartition_ReturnsGpt()
        {
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { LinuxFsGuid, EspGuid });

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        // ══════════════════════════════════════════════════════════════════
        //  GPT header validation
        // ══════════════════════════════════════════════════════════════════

        [TestMethod]
        public async Task Detect_GptHeaderWithWrongRevision_SkipsAndDefaultsToMbr()
        {
            // "EFI PART" present but revision is 0 — should be rejected
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid },
                gptRevision: 0x00000000);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        [TestMethod]
        public async Task Detect_GptHeaderWithWrongMyLba_SkipsAndDefaultsToMbr()
        {
            // MyLBA=99 instead of 1 — could be backup GPT or VHDX garbage
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid },
                myLba: 99);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        [TestMethod]
        public async Task Detect_GptHeaderWithMyLbaZero_SkipsAndDefaultsToMbr()
        {
            // MyLBA=0 (zeroed header)
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid },
                myLba: 0);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        [TestMethod]
        public async Task Detect_GptHeaderTooSmall_SkipsAndDefaultsToMbr()
        {
            // Header size < 92 bytes — invalid
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid },
                headerSize: 64);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Partition entry LBA and count from header
        // ══════════════════════════════════════════════════════════════════

        [TestMethod]
        public async Task Detect_EntriesAtLba2_StandardLayout_ReturnsGpt()
        {
            // Standard GPT: entries at LBA 2 (the default)
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid, LinuxFsGuid },
                entryStartLba: 2);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        [TestMethod]
        public async Task Detect_EntriesAtLba34_NonStandardLayout_ReturnsGpt()
        {
            // Some tools (e.g. parted) may place entries at LBA 34
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid, LinuxFsGuid },
                entryStartLba: 34);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        [TestMethod]
        public async Task Detect_EntriesAtLba34_NoEsp_ReturnsGptBios()
        {
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { BiosBootGuid, LinuxFsGuid },
                entryStartLba: 34);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT_BIOS", result);
        }

        [TestMethod]
        public async Task Detect_HeaderSaysZeroEntryCount_ReturnsGptBios()
        {
            // Entry count of 0 in header — no entries to scan
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid },
                entryCount: 0);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT_BIOS", result);
        }

        [TestMethod]
        public async Task Detect_HeaderSaysEntrySize256_ReturnsGpt()
        {
            // Non-standard entry size (256 bytes per entry) — should still find ESP
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid, LinuxFsGuid },
                entrySize: 256);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        [TestMethod]
        public async Task Detect_EntriesAtHighLba_BeyondFileSize_ReturnsGpt()
        {
            // Entry LBA points way beyond the file — conservative fallback assumes ESP present
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid },
                entryStartLba: 999999);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        // ══════════════════════════════════════════════════════════════════
        //  ESP position within many entries
        // ══════════════════════════════════════════════════════════════════

        [TestMethod]
        public async Task Detect_EspAsThirdOfFourPartitions_ReturnsGpt()
        {
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { LinuxFsGuid, LinuxSwapGuid, EspGuid, LinuxFsGuid });

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        [TestMethod]
        public async Task Detect_NoEspAmongManyPartitions_ReturnsGptBios()
        {
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { LinuxFsGuid, LinuxSwapGuid, MsBasicDataGuid, MsReservedGuid });

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT_BIOS", result);
        }

        [TestMethod]
        public async Task Detect_SingleEspPartition_ReturnsGpt()
        {
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid });

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        [TestMethod]
        public async Task Detect_EmptyPartitionTable_ReturnsGptBios()
        {
            // GPT header is valid but no partition entries at all
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: Array.Empty<byte[]>());

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT_BIOS", result);
        }

        // ══════════════════════════════════════════════════════════════════
        //  VHDX-like offsets (structures not at start of file)
        // ══════════════════════════════════════════════════════════════════

        [TestMethod]
        public async Task Detect_GptAt512KbOffset_ReturnsGpt()
        {
            // Virtual disk data starts at 512KB into the VHDX container
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid, LinuxFsGuid },
                diskBaseOffset: 0x80000); // 512KB

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        [TestMethod]
        public async Task Detect_GptAt8MbOffset_LikeUbuntuVhdx_ReturnsGpt()
        {
            // Simulates the Ubuntu 24.04 Hyper-V VHDX where MBR was found at 0x800000
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid, LinuxFsGuid },
                diskBaseOffset: 0x800000); // 8MB

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        [TestMethod]
        public async Task Detect_GptAt8MbOffset_NoEsp_ReturnsGptBios()
        {
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { BiosBootGuid, LinuxFsGuid },
                diskBaseOffset: 0x800000);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT_BIOS", result);
        }

        [TestMethod]
        public async Task Detect_GptAt8MbOffset_EntriesAtLba34_ReturnsGpt()
        {
            // Combines high offset with non-standard entry LBA
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid, LinuxFsGuid },
                diskBaseOffset: 0x800000, entryStartLba: 34);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        // ══════════════════════════════════════════════════════════════════
        //  False positive rejection (VHDX metadata, backup headers)
        // ══════════════════════════════════════════════════════════════════

        [TestMethod]
        public async Task Detect_FakeEfiPartSignature_NoValidHeader_DefaultsToMbr()
        {
            // Random "EFI PART" bytes appear but with zeroed header fields
            string path = Path.Combine(_tempDir, "fake_gpt.vhdx");
            byte[] disk = new byte[0xB00000];

            // Write "EFI PART" at some offset (simulating VHDX internal data)
            int fakeOffset = 0x50000 + 512;
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(disk, fakeOffset);
            // All other header fields are zero → revision=0, myLba=0 → rejected

            await File.WriteAllBytesAsync(path, disk);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        [TestMethod]
        public async Task Detect_BackupGptHeader_MyLbaNotOne_Skipped()
        {
            // A backup GPT header has MyLBA = last LBA (not 1). It should be skipped,
            // and the real primary header (if present) should be found instead.
            string path = Path.Combine(_tempDir, "backup_gpt.vhdx");
            int scanStart = (int)PartitionSchemeDetector.ScanStartOffset;

            // Large enough for: backup fake header at scanStart, real header at scanStart + 4096
            int totalSize = scanStart + 0x10000;
            byte[] disk = new byte[totalSize];

            // ── Fake backup GPT at scanStart ──
            // MBR signature so the sector is scanned
            disk[scanStart + 0x1FE] = 0x55;
            disk[scanStart + 0x1FF] = 0xAA;
            disk[scanStart + 0x1BE + 4] = 0xEE; // protective

            // GPT signature at scanStart + 512
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(disk, scanStart + 512);
            BitConverter.GetBytes(0x00010000u).CopyTo(disk, scanStart + 512 + 8);   // revision OK
            BitConverter.GetBytes(92u).CopyTo(disk, scanStart + 512 + 12);           // header size OK
            BitConverter.GetBytes((ulong)99999).CopyTo(disk, scanStart + 512 + 24);  // MyLBA = 99999 (backup!)
            // → Should be SKIPPED

            // ── Real primary GPT at scanStart + 1024*4 (4096 bytes later) ──
            int realBase = scanStart + 4096;
            disk[realBase + 0x1FE] = 0x55;
            disk[realBase + 0x1FF] = 0xAA;
            disk[realBase + 0x1BE + 4] = 0xEE;

            // Valid primary GPT header
            int realGpt = realBase + 512;
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(disk, realGpt);
            BitConverter.GetBytes(0x00010000u).CopyTo(disk, realGpt + 8);   // revision
            BitConverter.GetBytes(92u).CopyTo(disk, realGpt + 12);           // header size
            BitConverter.GetBytes((ulong)1).CopyTo(disk, realGpt + 24);     // MyLBA = 1
            BitConverter.GetBytes((ulong)2).CopyTo(disk, realGpt + 72);     // entry start LBA = 2
            BitConverter.GetBytes(128u).CopyTo(disk, realGpt + 80);          // entry count
            BitConverter.GetBytes(128u).CopyTo(disk, realGpt + 84);          // entry size

            // Partition entry with ESP at realBase + 1024 (LBA 2 from MBR base)
            int entriesBase = realBase + 1024;
            EspGuid.CopyTo(disk, entriesBase);
            BitConverter.GetBytes((long)2048).CopyTo(disk, entriesBase + 32);
            BitConverter.GetBytes((long)4095).CopyTo(disk, entriesBase + 40);

            await File.WriteAllBytesAsync(path, disk);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        [TestMethod]
        public async Task Detect_MultipleEfiPartSignatures_FirstValidOneWins()
        {
            // Two "EFI PART" signatures at different offsets — the first one
            // with valid header fields should determine the result
            string path = Path.Combine(_tempDir, "multi_efi.vhdx");
            int scanStart = (int)PartitionSchemeDetector.ScanStartOffset;
            int totalSize = scanStart + 0x10000;
            byte[] disk = new byte[totalSize];

            // ── First valid GPT at scanStart (BiosBootOnly → GPT_BIOS) ──
            WriteGptDisk(disk, scanStart, new[] { BiosBootGuid, LinuxFsGuid });

            // ── Second valid GPT at scanStart + 8192 (with ESP → GPT) ── but won't be reached
            WriteGptDisk(disk, scanStart + 8192, new[] { EspGuid, LinuxFsGuid });

            await File.WriteAllBytesAsync(path, disk);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            // First valid match (GPT_BIOS) should win — scanner returns on first valid header
            Assert.AreEqual("GPT_BIOS", result);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Edge cases: small files, truncated data
        // ══════════════════════════════════════════════════════════════════

        [TestMethod]
        public async Task Detect_FileTooSmallForScan_DefaultsToMbr()
        {
            string path = Path.Combine(_tempDir, "tiny.vhdx");
            byte[] data = new byte[0x8000]; // 32KB, less than ScanStartOffset (64KB)
            await File.WriteAllBytesAsync(path, data);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        [TestMethod]
        public async Task Detect_FileExactlyScanStart_DefaultsToMbr()
        {
            string path = Path.Combine(_tempDir, "exact.vhdx");
            byte[] data = new byte[0x10000]; // Exactly 64KB
            await File.WriteAllBytesAsync(path, data);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        [TestMethod]
        public async Task Detect_GptWithEntriesTruncatedByEof_ReturnsGptBios()
        {
            // GPT header is valid but the file ends before entries can be read
            string path = Path.Combine(_tempDir, "truncated.vhdx");
            int scanStart = (int)PartitionSchemeDetector.ScanStartOffset;
            // Only enough for MBR + GPT header, not entries
            int totalSize = scanStart + 512 + 512 + 64; // entries area too small
            byte[] disk = new byte[totalSize];

            WriteGptDisk(disk, scanStart, new[] { EspGuid }, entryStartLba: 2);

            // But the file is too small for entry data to be read
            // (entries would be at scanStart + 1024, but there's only 64 bytes there)
            // → Scanner reads partial/zero data → no ESP found
            await File.WriteAllBytesAsync(path, disk);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT_BIOS", result);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Windows-style layouts (MS partitions)
        // ══════════════════════════════════════════════════════════════════

        [TestMethod]
        public async Task Detect_WindowsLayout_EspMsReservedBasicData_ReturnsGpt()
        {
            // Typical Windows GPT: ESP + MSR + C: + Recovery
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { EspGuid, MsReservedGuid, MsBasicDataGuid, MsBasicDataGuid });

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        [TestMethod]
        public async Task Detect_WindowsLayoutNoEsp_ReturnsGptBios()
        {
            // Windows MBR-to-GPT converted without ESP
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { MsReservedGuid, MsBasicDataGuid });

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT_BIOS", result);
        }

        // ══════════════════════════════════════════════════════════════════
        //  MBR edge cases
        // ══════════════════════════════════════════════════════════════════

        [TestMethod]
        public async Task Detect_MbrWithMultiplePartitions_ReturnsMbr()
        {
            string path = Path.Combine(_tempDir, "multi_mbr.vhdx");
            int scanStart = (int)PartitionSchemeDetector.ScanStartOffset;
            int totalSize = scanStart + 0x10000;
            byte[] disk = new byte[totalSize];

            disk[scanStart + 0x1FE] = 0x55;
            disk[scanStart + 0x1FF] = 0xAA;
            disk[scanStart + 0x1BE + 4] = 0x83; // Linux
            disk[scanStart + 0x1CE + 4] = 0x82; // Linux swap
            disk[scanStart + 0x1DE + 4] = 0x83; // Linux

            await File.WriteAllBytesAsync(path, disk);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        [TestMethod]
        public async Task Detect_MbrWithNtfs_ReturnsMbr()
        {
            string path = BuildDisk(protectiveMbr: false, gptHeader: false,
                mbrPartitionType: 0x07); // NTFS

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds a minimal fake VHDX-like file with partition structures
        /// placed at the specified disk base offset. Writes spec-compliant
        /// GPT header fields so the detector can read entry LBA, count, and size.
        /// </summary>
        private string BuildDisk(bool protectiveMbr, bool gptHeader,
            byte[][] partitionGuids = null, byte mbrPartitionType = 0xEE,
            int diskBaseOffset = -1, ulong entryStartLba = 2,
            uint? gptRevision = null, ulong? myLba = null,
            uint? headerSize = null, uint? entryCount = null, uint entrySize = 128)
        {
            string path = Path.Combine(_tempDir, $"disk_{Guid.NewGuid():N}.vhdx");

            if (diskBaseOffset < 0)
                diskBaseOffset = (int)PartitionSchemeDetector.ScanStartOffset;

            // Calculate total size: base + MBR + GPT header + entries at entryStartLba
            long entriesOffset = diskBaseOffset + (long)entryStartLba * PartitionSchemeDetector.SectorSize;
            int numGuids = partitionGuids?.Length ?? 0;
            long entriesEnd = entriesOffset + (numGuids * (long)entrySize) + 512;
            long minSize = Math.Max(diskBaseOffset + 2048, entriesEnd);
            // Ensure file extends past ScanStartOffset
            long totalSize = Math.Max(minSize, PartitionSchemeDetector.ScanStartOffset + 0x1000);
            byte[] disk = new byte[totalSize];

            // ── MBR at diskBaseOffset ──
            disk[diskBaseOffset + 0x1FE] = 0x55;
            disk[diskBaseOffset + 0x1FF] = 0xAA;

            if (protectiveMbr)
                disk[diskBaseOffset + 0x1BE + 4] = 0xEE;
            else
                disk[diskBaseOffset + 0x1BE + 4] = mbrPartitionType;

            // ── GPT header at diskBaseOffset + 512 ──
            if (gptHeader)
            {
                int gptOffset = diskBaseOffset + 512;
                WriteGptHeader(disk, gptOffset,
                    revision: gptRevision ?? 0x00010000u,
                    myLba: myLba ?? 1,
                    headerSize: headerSize ?? 92u,
                    entryStartLba: entryStartLba,
                    entryCount: entryCount ?? 128u,
                    entrySize: entrySize);

                // ── Partition entries at diskBaseOffset + entryStartLba * 512 ──
                if (partitionGuids != null)
                {
                    long entBase = diskBaseOffset + (long)entryStartLba * PartitionSchemeDetector.SectorSize;
                    for (int i = 0; i < partitionGuids.Length; i++)
                    {
                        long entryOffset = entBase + (i * (long)entrySize);
                        Array.Copy(partitionGuids[i], 0, disk, entryOffset, 16);

                        // Set non-zero LBAs so the entry looks valid
                        BitConverter.GetBytes((long)(2048 + i * 2048)).CopyTo(disk, entryOffset + 32);
                        BitConverter.GetBytes((long)(4095 + i * 2048)).CopyTo(disk, entryOffset + 40);
                    }
                }
            }

            File.WriteAllBytes(path, disk);
            return path;
        }

        /// <summary>
        /// Writes a minimal GPT layout (MBR + header + entries) starting at
        /// the given base offset within an existing byte array.
        /// </summary>
        private static void WriteGptDisk(byte[] disk, int baseOffset, byte[][] partitionGuids,
            ulong entryStartLba = 2, uint entrySize = 128)
        {
            // MBR
            disk[baseOffset + 0x1FE] = 0x55;
            disk[baseOffset + 0x1FF] = 0xAA;
            disk[baseOffset + 0x1BE + 4] = 0xEE;

            // GPT header
            WriteGptHeader(disk, baseOffset + 512,
                revision: 0x00010000u, myLba: 1, headerSize: 92u,
                entryStartLba: entryStartLba, entryCount: 128, entrySize: entrySize);

            // Entries
            long entBase = baseOffset + (long)entryStartLba * PartitionSchemeDetector.SectorSize;
            for (int i = 0; i < partitionGuids.Length; i++)
            {
                long entryOffset = entBase + (i * (long)entrySize);
                Array.Copy(partitionGuids[i], 0, disk, entryOffset, 16);
                BitConverter.GetBytes((long)(2048 + i * 2048)).CopyTo(disk, entryOffset + 32);
                BitConverter.GetBytes((long)(4095 + i * 2048)).CopyTo(disk, entryOffset + 40);
            }
        }

        /// <summary>
        /// Writes GPT header fields into the byte array at the specified offset.
        /// </summary>
        private static void WriteGptHeader(byte[] disk, int offset,
            uint revision, ulong myLba, uint headerSize,
            ulong entryStartLba, uint entryCount, uint entrySize)
        {
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(disk, offset);       // signature
            BitConverter.GetBytes(revision).CopyTo(disk, offset + 8);        // revision
            BitConverter.GetBytes(headerSize).CopyTo(disk, offset + 12);     // header size
            BitConverter.GetBytes(myLba).CopyTo(disk, offset + 24);          // MyLBA
            BitConverter.GetBytes(entryStartLba).CopyTo(disk, offset + 72);  // partition entry LBA
            BitConverter.GetBytes(entryCount).CopyTo(disk, offset + 80);     // number of entries
            BitConverter.GetBytes(entrySize).CopyTo(disk, offset + 84);      // size of each entry
        }
    }
}
