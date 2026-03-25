using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace VMCreate.Tests
{
    [TestClass]
    public class DiskFileDetectorTests
    {
        private DiskFileDetector _detector;
        private Mock<IExtractor> _mockExtractor;
        private string _tempDir;

        [TestInitialize]
        public void Setup()
        {
            _mockExtractor = new Mock<IExtractor>();
            _detector = new DiskFileDetector(
                _mockExtractor.Object,
                new Mock<ILogger<DiskFileDetector>>().Object);
            _tempDir = Path.Combine(Path.GetTempPath(), "DiskDetectorTest_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [TestMethod]
        public void FindDiskFile_VmdkInFlatDirectory_ReturnsVmdk()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "disk1.vmdk"), new byte[1024]);
            File.WriteAllText(Path.Combine(_tempDir, "manifest.mf"), "sha256");
            File.WriteAllText(Path.Combine(_tempDir, "descriptor.ovf"), "<xml/>");

            string result = _detector.FindDiskFile(_tempDir);

            Assert.IsTrue(result.EndsWith("disk1.vmdk", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void FindDiskFile_VhdInSubdirectory_ReturnsVhd()
        {
            var sub = Path.Combine(_tempDir, "inner");
            Directory.CreateDirectory(sub);
            File.WriteAllBytes(Path.Combine(sub, "GNS3 VM-disk001.vhd"), new byte[2048]);

            string result = _detector.FindDiskFile(_tempDir);

            Assert.IsTrue(result.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void FindDiskFile_MultipleDisks_ReturnsLargest()
        {
            File.WriteAllBytes(Path.Combine(_tempDir, "small.vmdk"), new byte[100]);
            File.WriteAllBytes(Path.Combine(_tempDir, "large.vmdk"), new byte[5000]);

            string result = _detector.FindDiskFile(_tempDir);

            Assert.IsTrue(result.Contains("large.vmdk"));
        }

        [TestMethod]
        public void FindDiskFile_PrefersVmdkOverVhd()
        {
            // VMDK has higher priority than VHD in the extension list
            File.WriteAllBytes(Path.Combine(_tempDir, "disk.vmdk"), new byte[100]);
            File.WriteAllBytes(Path.Combine(_tempDir, "disk.vhd"), new byte[100]);

            string result = _detector.FindDiskFile(_tempDir);

            Assert.IsTrue(result.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void FindDiskFile_NestedArchive_ExtractsAndFinds()
        {
            // Place an OVA in the directory (no disk files yet)
            string ovaPath = Path.Combine(_tempDir, "image.ova");
            File.WriteAllBytes(ovaPath, new byte[512]);

            // The detector moves the archive to a temp location before extracting,
            // so the extractor receives a path outside the extract directory.
            string expectedTempPath = Path.Combine(Path.GetTempPath(), "image.ova");

            _mockExtractor
                .Setup(e => e.Extract(expectedTempPath, _tempDir, It.IsAny<CancellationToken>(), It.IsAny<IProgress<CreateVMProgressInfo>>()))
                .Callback(() =>
                {
                    // Simulate extraction producing a disk file
                    Directory.CreateDirectory(_tempDir);
                    File.WriteAllBytes(Path.Combine(_tempDir, "disk.vmdk"), new byte[1024]);
                });

            string result = _detector.FindDiskFile(_tempDir);

            Assert.IsTrue(result.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase));
            _mockExtractor.Verify(e => e.Extract(expectedTempPath, _tempDir, It.IsAny<CancellationToken>(), It.IsAny<IProgress<CreateVMProgressInfo>>()), Times.Once);
            // The original OVA should no longer be in the extract directory
            Assert.IsFalse(File.Exists(ovaPath));
        }

        [TestMethod]
        public void FindDiskFile_EmptyDirectory_Throws()
        {
            Assert.Throws<FileNotFoundException>(() => _detector.FindDiskFile(_tempDir));
        }

        [TestMethod]
        public void FindDiskFile_OnlyUnsupportedFiles_Throws()
        {
            File.WriteAllText(Path.Combine(_tempDir, "readme.txt"), "hello");
            File.WriteAllText(Path.Combine(_tempDir, "manifest.mf"), "sha256");

            Assert.Throws<FileNotFoundException>(() => _detector.FindDiskFile(_tempDir));
        }

        // ── DetectFileType static method ─────────────────────────────────

        [TestMethod]
        [DataRow("C:\\temp\\disk.vmdk", "VMDK")]
        [DataRow("/tmp/disk.qcow2", "QCOW2")]
        [DataRow("C:\\img\\disk.vhdx", "VHDX")]
        [DataRow("disk.vhd", "VHD")]
        [DataRow("install.iso", "ISO")]
        [DataRow("readme.txt", "Other")]
        [DataRow("", "Unknown")]
        [DataRow(null, "Unknown")]
        public void DetectFileType_VariousExtensions_ReturnsExpected(string path, string expected)
        {
            Assert.AreEqual(expected, DiskFileDetector.DetectFileType(path));
        }
    }
}
