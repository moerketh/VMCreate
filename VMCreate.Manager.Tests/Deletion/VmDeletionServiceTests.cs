using Moq;
using VMCreate.Manager.Deletion;
using VMCreate.Manager.HyperV;
using VMCreate.Metadata;

namespace VMCreate.Manager.Tests.Deletion
{
    [TestClass]
    public class VmDeletionServiceTests
    {
        private static readonly Guid Vm = Guid.NewGuid();

        private string _dir = "";
        private JsonVmMetadataStore _store = null!;
        private Mock<IHyperVHost> _host = null!;
        private ushort _state = 3; // off
        private List<RawAttachment> _attachments = new();

        private sealed class DeletingRemover : IFileRemover
        {
            public List<string> Removed { get; } = new();
            public void Remove(string path, bool permanently)
            {
                File.Delete(path);
                Removed.Add(path);
            }
        }

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "VMCreate.Manager.Tests", Guid.NewGuid().ToString("N"));
            _store = new JsonVmMetadataStore(Path.Combine(_dir, "metadata.json"));
            _host = new Mock<IHyperVHost>();
            _host.Setup(h => h.ReadSnapshotAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new HostSnapshot(
                    new[] { new RawVm(Vm, "victim", _state, 0, 2, null, "", null) },
                    Array.Empty<RawVmSummary>(), Array.Empty<RawGuestNetworkAdapter>(), Array.Empty<RawSwitchPort>(),
                    Array.Empty<RawSwitch>(), Array.Empty<RawMemorySetting>(), Array.Empty<RawCheckpoint>()));
            _host.Setup(h => h.ReadStorageGraphAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new StorageGraph(
                    _attachments.ToList(),
                    _attachments.ToDictionary(a => a.Path, a => new RawStorageFile(a.Path, File.Exists(a.Path), 3, null, 10), StringComparer.OrdinalIgnoreCase),
                    new[] { new RawVmStorageRoots(Vm, VmFolder, VmFolder, VmFolder) },
                    new[] { Path.Combine(_dir, "default") }));
            _host.Setup(h => h.DestroyVmAsync(Vm, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }

        private string VmFolder => Path.Combine(_dir, "victim");

        private string CreateDisk(string relative)
        {
            string path = Path.Combine(VmFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "disk");
            _attachments.Add(new RawAttachment(Vm, null, path, AttachedMediaKind.VirtualHardDisk));
            return path;
        }

        [TestMethod]
        public async Task Execute_DestroysVm_RemovesFilesEmptyFoldersAndMetadata()
        {
            string disk = CreateDisk(@"Virtual Hard Disks\victim.vhdx");
            Directory.CreateDirectory(Path.Combine(VmFolder, "Virtual Machines")); // left empty by DestroySystem
            await _store.UpdateAsync(doc => doc.GetOrAddVm(Vm).Description = "x");
            var remover = new DeletingRemover();
            var service = new VmDeletionService(_host.Object, _store, remover);

            var plan = await service.PlanAsync(Vm);
            var result = await service.ExecuteAsync(plan, permanently: false);

            _host.Verify(h => h.DestroyVmAsync(Vm, It.IsAny<CancellationToken>()), Times.Once);
            CollectionAssert.AreEqual(new[] { disk }, remover.Removed);
            Assert.IsTrue(result.Complete);
            Assert.IsFalse(Directory.Exists(VmFolder));
            Assert.IsNull((await _store.LoadAsync()).FindVm(Vm));
        }

        [TestMethod]
        public async Task Execute_KeepsFoldersThatStillHoldFiles()
        {
            CreateDisk("victim.vhdx");
            string stray = Path.Combine(VmFolder, "notes.txt");
            File.WriteAllText(stray, "mine");
            var service = new VmDeletionService(_host.Object, _store, new DeletingRemover());

            var result = await service.ExecuteAsync(await service.PlanAsync(Vm), permanently: true);

            Assert.IsTrue(File.Exists(stray));
            Assert.AreEqual(0, result.RemovedFolders.Count);
        }

        [TestMethod]
        public async Task Execute_RefusesWhenTheDisksChangedSincePreview()
        {
            CreateDisk("a.vhdx");
            var service = new VmDeletionService(_host.Object, _store, new DeletingRemover());
            var plan = await service.PlanAsync(Vm);
            CreateDisk("b.vhdx");

            await Assert.ThrowsExactlyAsync<DeletionRefusedException>(() => service.ExecuteAsync(plan, permanently: true));
            _host.Verify(h => h.DestroyVmAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task Execute_RefusesWhenTheVmWasStartedOrProtectedSincePreview()
        {
            CreateDisk("a.vhdx");
            var service = new VmDeletionService(_host.Object, _store, new DeletingRemover());
            var plan = await service.PlanAsync(Vm);

            _state = 2; // running
            await Assert.ThrowsExactlyAsync<DeletionRefusedException>(() => service.ExecuteAsync(plan, permanently: true));
            _state = 3;
            await _store.UpdateAsync(doc => doc.GetOrAddVm(Vm).Protected = true);
            await Assert.ThrowsExactlyAsync<DeletionRefusedException>(() => service.ExecuteAsync(plan, permanently: true));
            _host.Verify(h => h.DestroyVmAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task Execute_ReportsFilesThatCouldNotBeRemoved()
        {
            string disk = CreateDisk("a.vhdx");
            var remover = new Mock<IFileRemover>();
            remover.Setup(r => r.Remove(disk, false)).Throws(new OperationCanceledException("declined"));
            var service = new VmDeletionService(_host.Object, _store, remover.Object);

            var result = await service.ExecuteAsync(await service.PlanAsync(Vm), permanently: false);

            Assert.IsFalse(result.Complete);
            Assert.AreEqual(disk, result.FailedFiles.Single().Path);
            Assert.IsTrue(File.Exists(disk));
        }
    }
}
