using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Moq;
using VMCreate.Manager.Archive;
using VMCreate.Manager.HyperV;
using VMCreate.Metadata;

namespace VMCreate.Manager.Tests.Archive
{
    [TestClass]
    public class FolderArchiverTests
    {
        private string _dir = "";
        private Mock<IHyperVHost> _host = null!;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "VMCreate.Manager.Tests", Guid.NewGuid().ToString("N"));
            _host = new Mock<IHyperVHost>();
            // A fake export: one disk file whose content is the VM name.
            _host.Setup(h => h.ExportVmAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((Guid id, string dir, CancellationToken _) =>
                {
                    Directory.CreateDirectory(Path.Combine(dir, "Virtual Hard Disks"));
                    File.WriteAllText(Path.Combine(dir, "Virtual Hard Disks", "disk.vhdx"), id.ToString());
                    return Task.CompletedTask;
                });
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }

        [TestMethod]
        public async Task Archive_ExportsEachVm_AndWritesManifestWithChecksumsAndMetadata()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var meta = new VmMetadata
            {
                VmId = a,
                Description = "jump box",
                Tags = { "acme" },
                Credential = new StoredCredential { Username = "kali", ProtectedPassword = "SECRET-BLOB" },
            };

            var result = await new FolderArchiver(_host.Object).ArchiveAsync("Engagements/ACME",
                new[] { new ArchiveItem(a, "kali:01", meta), new ArchiveItem(b, "kali:01", null) }, _dir);

            StringAssert.StartsWith(Path.GetFileName(result.Directory), "Engagements-ACME_");
            var manifest = JsonSerializer.Deserialize<ArchiveManifest>(File.ReadAllText(result.ManifestPath),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            Assert.AreEqual("Engagements/ACME", manifest.Folder);
            Assert.AreEqual(2, manifest.Vms.Count);
            // Same names, invalid characters: two distinct, valid directories.
            Assert.AreEqual("kali_01", manifest.Vms[0].Directory);
            Assert.AreEqual("kali_01 (2)", manifest.Vms[1].Directory);

            var first = manifest.Vms[0];
            Assert.AreEqual("jump box", first.Description);
            Assert.AreEqual("kali", first.Username);
            var file = first.Files.Single();
            Assert.AreEqual(Path.Combine("kali_01", "Virtual Hard Disks", "disk.vhdx"), file.Path);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(a.ToString()))), file.Sha256);
            Assert.IsFalse(File.ReadAllText(result.ManifestPath).Contains("SECRET-BLOB"), "the DPAPI blob must never be archived");
        }

        [TestMethod]
        public async Task Archive_RecordsAFailedExport_AndContinues()
        {
            var bad = Guid.NewGuid();
            var good = Guid.NewGuid();
            _host.Setup(h => h.ExportVmAsync(bad, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HyperVOperationException("disk full"));

            var result = await new FolderArchiver(_host.Object).ArchiveAsync("Labs",
                new[] { new ArchiveItem(bad, "bad", null), new ArchiveItem(good, "good", null) }, _dir);

            Assert.AreEqual("disk full", result.Failed.Single().Error);
            Assert.AreEqual(good, result.Exported.Single().VmId);
            Assert.IsTrue(File.Exists(result.ManifestPath));
        }

        [TestMethod]
        public async Task Archive_Cancelled_KeepsFinishedVms_RemovesThePartialOne_AndStops()
        {
            var done = Guid.NewGuid();
            var partial = Guid.NewGuid();
            var never = Guid.NewGuid();
            using var cts = new CancellationTokenSource();
            // The second export writes half a disk, then sees the cancel.
            _host.Setup(h => h.ExportVmAsync(partial, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, string dir, CancellationToken ct) =>
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "half.vhdx"), "…");
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                });

            var result = await new FolderArchiver(_host.Object).ArchiveAsync("Labs",
                new[] { new ArchiveItem(done, "done", null), new ArchiveItem(partial, "partial", null), new ArchiveItem(never, "never", null) },
                _dir, cancellationToken: cts.Token);

            Assert.IsTrue(result.Cancelled);
            Assert.AreEqual(done, result.Exported.Single().VmId);
            Assert.IsTrue(Directory.Exists(Path.Combine(result.Directory, "done")));
            Assert.IsFalse(Directory.Exists(Path.Combine(result.Directory, "partial")), "the partial export is removed");
            Assert.AreEqual(2, result.Manifest.Vms.Count, "VMs after the cancel are not attempted");
            _host.Verify(h => h.ExportVmAsync(never, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            StringAssert.Contains(File.ReadAllText(result.ManifestPath), "\"cancelled\": true");
        }

        [TestMethod]
        public void ArchiveDirectoryName_IsFolderPathAndTimestamp()
            => Assert.AreEqual("Engagements-ACME_20261101-1405",
                FolderArchiver.ArchiveDirectoryName("Engagements/ACME", new DateTimeOffset(2026, 11, 1, 14, 5, 0, TimeSpan.Zero)));
    }
}
