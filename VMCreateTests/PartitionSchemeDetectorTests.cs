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
            // Disk with VHDX-like padding but no partition signatures
            string path = Path.Combine(_tempDir, "empty.vhdx");
            byte[] data = new byte[0xB00000]; // 11MB of zeros
            await File.WriteAllBytesAsync(path, data);

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("MBR", result);
        }

        [TestMethod]
        public async Task Detect_GptWithEspAsSecondPartition_ReturnsGpt()
        {
            // ESP can appear as any partition entry, not just the first
            string path = BuildDisk(protectiveMbr: true, gptHeader: true,
                partitionGuids: new[] { LinuxFsGuid, EspGuid });

            string result = await _detector.DetectPartitionSchemeAsync(path);

            Assert.AreEqual("GPT", result);
        }

        /// <summary>
        /// Builds a minimal fake VHDX-like file with partition structures
        /// placed at the scan start offset (0x10000).
        /// </summary>
        private string BuildDisk(bool protectiveMbr, bool gptHeader,
            byte[][] partitionGuids = null, byte mbrPartitionType = 0xEE)
        {
            string path = Path.Combine(_tempDir, $"disk_{Guid.NewGuid():N}.vhdx");
            const int scanStart = 0x10000; // Must match PartitionSchemeDetector.ScanStartOffset

            // Need enough space: scanStart + MBR(512) + GPT header(512) + entries
            int totalSize = scanStart + 512 + 512 + (16 * 128) + 512;
            byte[] disk = new byte[totalSize];

            // ── MBR at scanStart ──
            int mbrOffset = scanStart;

            // MBR boot signature
            disk[mbrOffset + 0x1FE] = 0x55;
            disk[mbrOffset + 0x1FF] = 0xAA;

            // First partition entry at 0x1BE
            if (protectiveMbr)
            {
                disk[mbrOffset + 0x1BE + 4] = 0xEE; // Protective MBR type
            }
            else
            {
                disk[mbrOffset + 0x1BE + 4] = mbrPartitionType;
            }

            // ── GPT header at scanStart + 512 ──
            if (gptHeader)
            {
                int gptOffset = scanStart + 512;
                byte[] sig = Encoding.ASCII.GetBytes("EFI PART");
                Array.Copy(sig, 0, disk, gptOffset, sig.Length);

                // ── GPT partition entries at scanStart + 1024 ──
                if (partitionGuids != null)
                {
                    int entriesOffset = scanStart + 1024;
                    for (int i = 0; i < partitionGuids.Length; i++)
                    {
                        int entryBase = entriesOffset + (i * 128);
                        Array.Copy(partitionGuids[i], 0, disk, entryBase, 16);

                        // Set non-zero LBAs so the entry looks valid
                        BitConverter.GetBytes((long)(2048 + i * 2048)).CopyTo(disk, entryBase + 32);
                        BitConverter.GetBytes((long)(4095 + i * 2048)).CopyTo(disk, entryBase + 40);
                    }
                }
            }

            File.WriteAllBytes(path, disk);
            return path;
        }
    }
}
