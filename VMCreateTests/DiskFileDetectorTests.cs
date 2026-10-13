using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using VMCreate.MediaHandlers;

namespace VMCreate.Tests
{
    [TestClass]
    public class DiskFileDetectorTests
    {
        private DiskFileDetector _detector = null!;
        private Mock<IExtractor> _mockExtractor = null!;
        private string _tempDir = null!;
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
        [DataRow("C:\\temp\\disk.vmdk", DiskImageFormat.Vmdk)]
        [DataRow("/tmp/disk.qcow2", DiskImageFormat.Qcow2)]
        [DataRow("C:\\img\\disk.vhdx", DiskImageFormat.Vhdx)]
        [DataRow("disk.vhd", DiskImageFormat.Vhd)]
        [DataRow("install.iso", DiskImageFormat.Iso)]
        [DataRow("readme.txt", DiskImageFormat.Other)]
        [DataRow("", DiskImageFormat.Other)]
        [DataRow(null, DiskImageFormat.Other)]
        public void DetectFileType_VariousExtensions_ReturnsExpected(string path, DiskImageFormat expected)
        {
            Assert.AreEqual(expected, DiskFileDetector.DetectFileType(path));
        }

        // ── Cloud-image ".img" handling (Elastic SIEM appliance) ─────────

        [TestMethod]
        public void DetectFileType_Qcow2MagicInDotImgFile_ReturnsQcow2()
        {
            // Ubuntu cloud images ship qcow2 data as ".img".
            string path = Path.Combine(_tempDir, "noble-server-cloudimg-amd64.img");
            byte[] qcow2Header = { (byte)'Q', (byte)'F', (byte)'I', 0xFB, 0x00, 0x00, 0x00, 0x03 };
            File.WriteAllBytes(path, qcow2Header);

            Assert.AreEqual(DiskImageFormat.Qcow2, DiskFileDetector.DetectFileType(path));
        }

        [TestMethod]
        public void DetectFileType_UnknownExtensionNonQcow2LocalFile_StaysOther()
        {
            string path = Path.Combine(_tempDir, "random-data.img");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Assert.AreEqual(DiskImageFormat.Other, DiskFileDetector.DetectFileType(path));
        }

        [TestMethod]
        public void DetectFileType_DotImgUrlWithoutLocalFile_StaysOther()
        {
            // Pre-download gating (URI only): no bytes to sniff — the loader's
            // FileTypeOverride covers this case via ResolveDiskImageFormat.
            Assert.AreEqual(DiskImageFormat.Other,
                DiskFileDetector.DetectFileType("https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img"));
        }

        [TestMethod]
        public void ResolveDiskImageFormat_LoaderOverrideWinsOverUriExtension()
        {
            var item = new GalleryItem
            {
                Name = "Elastic SIEM",
                Publisher = "t",
                Description = "t",
                DiskUri = "https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img",
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = "24.04",
                FileTypeOverride = "QCOW2",
            };

            Assert.AreEqual(DiskImageFormat.Qcow2,
                DiskFileDetector.ResolveDiskImageFormat(item, item.DiskUri));
        }

        [TestMethod]
        public void ResolveDiskImageFormat_NoOverride_FallsBackToDetection()
        {
            var item = new GalleryItem
            {
                Name = "x",
                Publisher = "t",
                Description = "t",
                DiskUri = "install.iso",
                SecureBoot = "false",
                EnhancedSessionTransportType = "HvSocket",
                Version = "1",
            };

            Assert.AreEqual(DiskImageFormat.Iso,
                DiskFileDetector.ResolveDiskImageFormat(item, item.DiskUri));
        }
    }
}
