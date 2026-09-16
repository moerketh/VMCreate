using Microsoft.Extensions.Logging;
using Moq;
using System.Security.Cryptography;
using System.Text;

namespace VMCreate.Tests
{
    /// <summary>
    /// Regression tests for XzFileExtractor (XZ.NET + bundled liblzma.dll).
    /// XZ.NET is deliberately kept over SharpCompress 0.48.1: SharpCompress's
    /// XZStream silently truncates concatenated multi-stream .xz files, while
    /// XZ.NET handles all stream combinations. These tests pin decompression
    /// correctness (incl. multi-stream) so the native dependency is never removed
    /// or swapped without noticing the regression.
    /// Corpus: "VMCreate XZ regression corpus. " × 64 (1,984 bytes),
    /// sha256 dc4102dff10353c9beaf900fec4a3d882f90052dbe126f8cf1ec10f443a558df,
    /// single-stream 116 B, two-member multi-stream 216 B (LZMA2, CRC64, xz 5.2 header).
    /// </summary>
    [TestClass]
    public class XzFileExtractorTests
    {
        // xz - base64 of single-stream frame
        private const string StdXzB64 =
            "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4Ae/ADJdACsTRG97CPY+BdNR3kIbtfpnRVEsjAuIrxF+"
            + "zADXLHiOKChZqPJ5x7aU/njEuXfwmwAAAAAAFTQuxQr+lbUAAU7ADwAAAExHcUexxGf7AgAAAAAEWVo=";

        // xz - base64 of two concatenated single-stream frames (split mid-array)
        private const string MultiXzB64 =
            "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4APfACtdACsTRG97CPY+BdNR3kIbtfpnRVEsjAuIrxF+"
            + "zADXLHiOKChZqPJ5xwlYKwAAAEIHQc9FrQzLAAFH4AcAAABvEQtkscRn+wIAAAAABFla"
            + "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4APfACtdACsTRG97CPY+BdNR3kIbtfpnRVEsjAuIrxF+"
            + "zADXLHiOKChZqPJ5xwlYKwAAAEIHQc9FrQzLAAFH4AcAAABvEQtkscRn+wIAAAAABFla";

        private static readonly byte[] Expected = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("VMCreate XZ regression corpus. ", 64)));

        private Mock<ILogger<XzFileExtractor>> _mockLogger = null!;
        private XzFileExtractor _extractor = null!;

        [TestInitialize]
        public void Setup()
        {
            _mockLogger = new Mock<ILogger<XzFileExtractor>>();
            _extractor = new XzFileExtractor(_mockLogger.Object);
        }

        private static (string ArchivePath, string ExtractDir, string ExpectedOutPath) CreateTemp(string xzB64, string fileName)
        {
            string archivePath = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllBytes(archivePath, Convert.FromBase64String(xzB64));
            string extractDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            return (archivePath, extractDir, Path.Combine(extractDir, Path.GetFileNameWithoutExtension(fileName)));
        }

        [TestMethod]
        public void Extract_SingleStreamXz_RoundTripsReportsProgressAndKeepsDoubleExtension()
        {
            (string archivePath, string extractDir, string expectedOut) = CreateTemp(StdXzB64, "parrot.vmdk.xz");
            var reports = new List<CreateVMProgressInfo>();
            try
            {
                _extractor.Extract(archivePath, extractDir, CancellationToken.None,
                    new ImmediateProgress<CreateVMProgressInfo>(reports.Add));

                Assert.IsTrue(File.Exists(expectedOut),
                    $"Expected output to strip only the .xz extension, leaving 'parrot.vmdk' in {extractDir}");
                Assert.AreEqual(Expected.Length, new FileInfo(expectedOut).Length);
                Assert.IsTrue(SHA256.HashData(File.ReadAllBytes(expectedOut)).AsSpan().SequenceEqual(
                    SHA256.HashData(Expected)), "Round-tripped content must match the corpus");

                Assert.IsTrue(reports.Any(r => r.Phase == VmDeploymentPhase.Extract), "Expected Extract phase reports");
                Assert.IsTrue(reports.Count(r => r.ProgressPercentage > 0 && r.ProgressPercentage < 100) > 0,
                    "Expected intermediate progress reports");
                Assert.AreEqual(100, reports.Last().ProgressPercentage, "Final report must be 100%");
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            }
        }

        [TestMethod]
        public void Extract_MultiStreamXz_DecodesAllMembers()
        {
            // The case that silently truncates under SharpCompress XZStream — must never regress.
            (string archivePath, string extractDir, string expectedOut) = CreateTemp(MultiXzB64, "multi-disk.vmdk.xz");
            try
            {
                _extractor.Extract(archivePath, extractDir, CancellationToken.None, null);

                Assert.IsTrue(File.Exists(expectedOut), "Expected decompressed output to exist");
                byte[] actual = File.ReadAllBytes(expectedOut);
                Assert.AreEqual(Expected.Length, actual.Length,
                    $"Multi-stream must produce ALL members ({Expected.Length} bytes), got {actual.Length}");
                CollectionAssert.AreEqual(Expected, actual, "Multi-stream content must round-trip exactly");
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            }
        }

        [TestMethod]
        public void Extract_NonXzFile_ThrowsInvalidOperationException()
        {
            string archivePath = Path.Combine(Path.GetTempPath(), "notanxz.bin");
            File.WriteAllBytes(archivePath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 });
            string extractDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                _extractor.Extract(archivePath, extractDir, CancellationToken.None, null);
                Assert.Fail("Expected InvalidOperationException for bad magic bytes");
            }
            catch (InvalidOperationException)
            {
                // expected: magic-byte validation
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            }
        }
    }
}