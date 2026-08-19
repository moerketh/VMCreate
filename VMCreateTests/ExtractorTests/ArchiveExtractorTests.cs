using Microsoft.Extensions.Logging;
using Moq;
using System.IO.Compression;

namespace VMCreate.Tests
{
    /// <summary>
    /// Synchronous IProgress<T> implementation for deterministic unit testing.
    /// Unlike Progress<T>, which posts to the synchronization context asynchronously,
    /// this invokes the handler immediately on the calling thread.
    /// </summary>
    public class ImmediateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public ImmediateProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    [TestClass]
    public class ArchiveExtractorTests
    {
        private Mock<ILogger<ArchiveExtractor>> _mockLogger;
        private ArchiveExtractor _extractor;

        [TestInitialize]
        public void Setup()
        {
            _mockLogger = new Mock<ILogger<ArchiveExtractor>>();
            _extractor = new ArchiveExtractor(_mockLogger.Object);
        }

        private static string CreateZipArchive(Dictionary<string, byte[]> entries)
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".zip");
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var kvp in entries)
                {
                    var zipEntry = zip.CreateEntry(kvp.Key, CompressionLevel.NoCompression);
                    using (var es = zipEntry.Open())
                    {
                        es.Write(kvp.Value, 0, kvp.Value.Length);
                    }
                }
            }
            return path;
        }

        private static string CreateGzipArchive(byte[] content)
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".gz");
            using (var fs = File.Create(path))
            using (var gz = new GZipStream(fs, CompressionLevel.NoCompression))
            {
                gz.Write(content, 0, content.Length);
            }
            return path;
        }

        [TestMethod]
        public void Extract_SingleFileZip_ReportsProgressAndCompletesAt100()
        {
            byte[] data = new byte[100 * 1024]; // 100 KB
            new Random(42).NextBytes(data);
            string archivePath = CreateZipArchive(new Dictionary<string, byte[]> { { "test.bin", data } });
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            var reports = new List<CreateVMProgressInfo>();
            var progress = new ImmediateProgress<CreateVMProgressInfo>(r => reports.Add(r));

            try
            {
                _extractor.Extract(archivePath, extractPath, CancellationToken.None, progress);

                Assert.IsTrue(reports.Count > 0, "Expected at least one progress report");
                Assert.IsTrue(reports.Any(r => r.Phase == VmDeploymentPhase.Extract), "Expected phase to be Extract");
                Assert.IsTrue(reports.Any(r => r.ProgressPercentage > 0), "Expected intermediate progress > 0");
                Assert.AreEqual(100, reports.Last().ProgressPercentage, "Expected final report to be 100%");

                string extractedFile = Path.Combine(extractPath, "test.bin");
                Assert.IsTrue(File.Exists(extractedFile), "Expected extracted file to exist");
                CollectionAssert.AreEqual(data, File.ReadAllBytes(extractedFile), "Extracted content must match");
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
        }

        [TestMethod]
        public void Extract_MultiEntryZip_ProgressAdvancesMonotonically()
        {
            var entries = new Dictionary<string, byte[]>();
            for (int i = 0; i < 5; i++)
            {
                byte[] data = new byte[64 * 1024];
                new Random(i).NextBytes(data);
                entries[$"file{i}.bin"] = data;
            }

            string archivePath = CreateZipArchive(entries);
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            var reports = new List<CreateVMProgressInfo>();
            var progress = new ImmediateProgress<CreateVMProgressInfo>(r => reports.Add(r));

            try
            {
                _extractor.Extract(archivePath, extractPath, CancellationToken.None, progress);

                Assert.AreEqual(100, reports.Last().ProgressPercentage, "Final progress must be 100%");

                for (int i = 1; i < reports.Count; i++)
                {
                    Assert.IsTrue(reports[i].ProgressPercentage >= reports[i - 1].ProgressPercentage,
                        $"Progress must not decrease: {reports[i - 1].ProgressPercentage}% -> {reports[i].ProgressPercentage}%");
                }
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
        }

        [TestMethod]
        public void Extract_LargeSingleEntryZip_MultipleIntermediateReports()
        {
            byte[] data = new byte[10 * 1024 * 1024]; // 10 MB
            new Random(123).NextBytes(data);
            string archivePath = CreateZipArchive(new Dictionary<string, byte[]> { { "large.bin", data } });
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            var reports = new List<CreateVMProgressInfo>();
            var progress = new ImmediateProgress<CreateVMProgressInfo>(r => reports.Add(r));

            try
            {
                _extractor.Extract(archivePath, extractPath, CancellationToken.None, progress);

                // With throttling (≥1% change or 200ms), a 10 MB file should produce ~10-100 reports
                // depending on speed. We just need > 2 to prove it's not just 0% → 100%.
                Assert.IsTrue(reports.Count > 2, $"Expected multiple intermediate reports, got {reports.Count}");
                Assert.IsTrue(reports.Any(r => r.ProgressPercentage > 0 && r.ProgressPercentage < 100),
                    "Expected at least one intermediate progress report");
                Assert.AreEqual(100, reports.Last().ProgressPercentage);
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
        }

        [TestMethod]
        public void Extract_GzipKeylessEntry_ReportsProgressAndExtractsCorrectly()
        {
            byte[] data = new byte[256 * 1024];
            new Random(99).NextBytes(data);
            string archivePath = CreateGzipArchive(data);
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            var reports = new List<CreateVMProgressInfo>();
            var progress = new ImmediateProgress<CreateVMProgressInfo>(r => reports.Add(r));

            try
            {
                _extractor.Extract(archivePath, extractPath, CancellationToken.None, progress);

                Assert.IsTrue(reports.Count > 0, "Expected at least one progress report for gzip");
                Assert.AreEqual(100, reports.Last().ProgressPercentage, "Final progress must be 100%");

                string expectedName = Path.GetFileNameWithoutExtension(archivePath);
                string extractedFile = Path.Combine(extractPath, expectedName);
                Assert.IsTrue(File.Exists(extractedFile), $"Expected extracted file {expectedName} to exist");
                byte[] extracted = File.ReadAllBytes(extractedFile);
                Assert.IsTrue(extracted.Length > 0, "Extracted gzip content should not be empty");
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
        }

        [TestMethod]
        public void Extract_NestedDirectories_CreatesCorrectPaths()
        {
            var entries = new Dictionary<string, byte[]>
            {
                { "dir1/dir2/file.txt", System.Text.Encoding.UTF8.GetBytes("nested content") },
                { "root.txt", System.Text.Encoding.UTF8.GetBytes("root content") }
            };

            string archivePath = CreateZipArchive(entries);
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            try
            {
                _extractor.Extract(archivePath, extractPath, CancellationToken.None, new ImmediateProgress<CreateVMProgressInfo>(_ => { }));

                string nestedFile = Path.Combine(extractPath, "dir1", "dir2", "file.txt");
                string rootFile = Path.Combine(extractPath, "root.txt");

                Assert.IsTrue(File.Exists(nestedFile), "Nested file should exist");
                Assert.IsTrue(File.Exists(rootFile), "Root file should exist");
                Assert.AreEqual("nested content", File.ReadAllText(nestedFile));
                Assert.AreEqual("root content", File.ReadAllText(rootFile));
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
        }

        [TestMethod]
        public void Extract_CancellationMidWay_ThrowsOperationCanceledException()
        {
            byte[] data = new byte[10 * 1024 * 1024]; // 10 MB to give time to cancel
            new Random(77).NextBytes(data);
            string archivePath = CreateZipArchive(new Dictionary<string, byte[]> { { "big.bin", data } });
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var cts = new CancellationTokenSource();

            try
            {
                var progress = new ImmediateProgress<CreateVMProgressInfo>(r =>
                {
                    if (r.ProgressPercentage >= 10)
                    {
                        cts.Cancel();
                    }
                });

                Assert.Throws<OperationCanceledException>(() =>
                    _extractor.Extract(archivePath, extractPath, cts.Token, progress));
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
        }

        [TestMethod]
        public void Extract_Throttling_ReducesReportCount()
        {
            byte[] data = new byte[2 * 1024 * 1024]; // 2 MB
            new Random(55).NextBytes(data);
            string archivePath = CreateZipArchive(new Dictionary<string, byte[]> { { "throttle.bin", data } });
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            var reports = new List<CreateVMProgressInfo>();
            var progress = new ImmediateProgress<CreateVMProgressInfo>(r => reports.Add(r));

            try
            {
                _extractor.Extract(archivePath, extractPath, CancellationToken.None, progress);

                // SharpCompress calls progress every ~128KB for ZIP. 2MB / 128KB ≈ 16 raw calls.
                // Throttling by 1% should keep it under ~110 for any reasonable size.
                // For 2MB, 100 percentage points = at most ~100 reports + some time-based.
                Assert.IsTrue(reports.Count <= 110,
                    $"Expected throttled reports ≤ 110, got {reports.Count}. Throttling is not working.");
                Assert.AreEqual(100, reports.Last().ProgressPercentage);
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
        }

        [TestMethod]
        public void Extract_PreserveFileTime_LastModifiedMatches()
        {
            var targetTime = new DateTime(2023, 5, 15, 12, 30, 0, DateTimeKind.Local);
            byte[] data = System.Text.Encoding.UTF8.GetBytes("time test");
            string archivePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".zip");
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            using (var fs = File.Create(archivePath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("timed.txt");
                entry.LastWriteTime = targetTime;
                using (var es = entry.Open())
                {
                    es.Write(data, 0, data.Length);
                }
            }

            try
            {
                _extractor.Extract(archivePath, extractPath, CancellationToken.None, new ImmediateProgress<CreateVMProgressInfo>(_ => { }));

                string extractedFile = Path.Combine(extractPath, "timed.txt");
                Assert.IsTrue(File.Exists(extractedFile));
                var actualTime = File.GetLastWriteTime(extractedFile);
                // Allow small tolerance due to filesystem precision
                Assert.IsTrue(Math.Abs((actualTime - targetTime).TotalSeconds) < 2,
                    $"Expected last write time near {targetTime}, got {actualTime}");
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
        }

        [TestMethod]
        public void Extract_NonExistentFile_ThrowsFileNotFoundException()
        {
            string fakePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".zip");
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            Assert.Throws<FileNotFoundException>(() =>
                _extractor.Extract(fakePath, extractPath, CancellationToken.None, new ImmediateProgress<CreateVMProgressInfo>(_ => { })));
        }

        [TestMethod]
        public void Extract_ExistingExtractDirectory_IsRecreatedCleanly()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("fresh content");
            string archivePath = CreateZipArchive(new Dictionary<string, byte[]> { { "fresh.txt", data } });
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            // Pre-create directory with stale content
            Directory.CreateDirectory(extractPath);
            File.WriteAllText(Path.Combine(extractPath, "stale.txt"), "old");

            try
            {
                _extractor.Extract(archivePath, extractPath, CancellationToken.None, new ImmediateProgress<CreateVMProgressInfo>(_ => { }));

                Assert.IsFalse(File.Exists(Path.Combine(extractPath, "stale.txt")), "Stale file should be removed");
                Assert.IsTrue(File.Exists(Path.Combine(extractPath, "fresh.txt")), "Fresh file should exist");
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
        }

        /// <summary>
        /// Regression guard for the disk-space pre-flight check: extracting an archive
        /// whose uncompressed size exceeds the available free space on the destination
        /// drive must throw <see cref="IOException"/> with a clear, actionable message
        /// before writing any files. This catches the failure the user hit when the D:
        /// drive ran out of space during a Parrot qcow2 extraction (needed 15.2 GB, only
        /// 6.4 GB available).
        /// </summary>
        /// <remarks>
        /// Uses the test-friendly <see cref="ArchiveExtractor"/> constructor that injects
        /// a fake disk-space lookup, so the test is fully deterministic and fast: it
        /// creates a tiny archive (a few KB) but reports the destination drive as having
        /// only 1 byte free. No multi-GB allocation is needed.
        /// </remarks>
        [TestMethod]
        public void Extract_InsufficientDiskSpace_ThrowsIOExceptionWithClearMessage()
        {
            // A small, real archive — its uncompressed size is ~3 KB, which exceeds the
            // fake 1-byte free space, exercising the pre-flight check without big allocs.
            byte[] data = new byte[1024];
            new Random(7).NextBytes(data);
            string archivePath = CreateZipArchive(new Dictionary<string, byte[]> { { "small.bin", data } });
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            const string fakeDriveName = @"D:\";
            const long fakeFreeSpace = 1L; // 1 byte — always less than the archive's size
            var lowSpaceExtractor = new ArchiveExtractor(_mockLogger.Object,
                _ => (fakeFreeSpace, fakeDriveName));

            try
            {
                var ex = Assert.Throws<IOException>(() =>
                    lowSpaceExtractor.Extract(archivePath, extractPath, CancellationToken.None,
                        new ImmediateProgress<CreateVMProgressInfo>(_ => { })));

                StringAssert.Contains(ex.Message, "Not enough disk space",
                    "The error message must explain the disk-space problem so the user can act on it.");
                StringAssert.Contains(ex.Message, fakeDriveName,
                    "The error message must identify which drive is short of space.");
                StringAssert.Contains(ex.Message, "Free up space",
                    "The error message must tell the user how to recover.");
                StringAssert.Contains(ex.Message, "Need",
                    "The error message must state how much space is needed.");
                StringAssert.Contains(ex.Message, "only",
                    "The error message must state how much space is available.");

                // No partial files should be left behind — the pre-flight check runs
                // before any entry is written, so the extract directory should be empty
                // (or not exist beyond what SetupExtractDirectory created).
                Assert.IsFalse(Directory.Exists(extractPath) && Directory.EnumerateFiles(extractPath, "*", SearchOption.AllDirectories).Any(),
                    "No files should be extracted when the pre-flight disk-space check fails.");
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
        }

        /// <summary>
        /// Sanity check that the pre-flight check does NOT trip when there is ample
        /// free space — guards against a bug where the injected space lookup is
        /// compared the wrong way around.
        /// </summary>
        [TestMethod]
        public void Extract_SufficientDiskSpace_ProceedsWithoutIOException()
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("plenty of space");
            string archivePath = CreateZipArchive(new Dictionary<string, byte[]> { { "ok.txt", data } });
            string extractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            // Report a generous 1 TB free so the ~20-byte archive always fits.
            var ampleSpaceExtractor = new ArchiveExtractor(_mockLogger.Object,
                _ => (1_000_000_000_000L, @"C:\"));

            try
            {
                ampleSpaceExtractor.Extract(archivePath, extractPath, CancellationToken.None,
                    new ImmediateProgress<CreateVMProgressInfo>(_ => { }));

                string extractedFile = Path.Combine(extractPath, "ok.txt");
                Assert.IsTrue(File.Exists(extractedFile), "File should extract when there is enough space.");
                Assert.AreEqual("plenty of space", File.ReadAllText(extractedFile));
            }
            finally
            {
                File.Delete(archivePath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
        }
    }
}
