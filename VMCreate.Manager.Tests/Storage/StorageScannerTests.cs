using System.Diagnostics;
using Moq;
using VMCreate.Manager.Deletion;
using VMCreate.Manager.HyperV;
using VMCreate.Manager.Storage;
using VMCreate.Metadata;

namespace VMCreate.Manager.Tests.Storage
{
    [TestClass]
    public class StorageScannerTests
    {
        private static readonly Guid Vm = Guid.NewGuid();
        private static readonly Guid Checkpoint = Guid.NewGuid();

        private string _dir = "";
        private JsonVmMetadataStore _store = null!;
        private Mock<IHyperVHost> _host = null!;
        private readonly List<RawAttachment> _attachments = new();
        private readonly Dictionary<string, RawStorageFile> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<RawStorageFile> _inspected = new();

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
            Directory.CreateDirectory(HostRoot);
            Directory.CreateDirectory(VmFolder);
            _store = new JsonVmMetadataStore(Path.Combine(_dir, "metadata.json"));
            _host = new Mock<IHyperVHost>();
            _host.Setup(h => h.ReadSnapshotAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new HostSnapshot(
                    new[] { new RawVm(Vm, "lab", 3, 0, 2, null, "", null) },
                    Array.Empty<RawVmSummary>(), Array.Empty<RawGuestNetworkAdapter>(), Array.Empty<RawSwitchPort>(),
                    Array.Empty<RawSwitch>(), Array.Empty<RawMemorySetting>(), Array.Empty<RawCheckpoint>()));
            _host.Setup(h => h.ReadStorageGraphAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Graph());
            _host.Setup(h => h.InspectVirtualDisksAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<string> paths, CancellationToken _) =>
                    paths.Select(p => _inspected.FirstOrDefault(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))
                                      ?? new RawStorageFile(p, true, 3, null, 1)).ToList());
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }

        private string HostRoot => Path.Combine(_dir, "Hyper-V");
        private string VmFolder => Path.Combine(HostRoot, "lab");

        private StorageGraph Graph() => new(
            _attachments.ToList(),
            new Dictionary<string, RawStorageFile>(_files, StringComparer.OrdinalIgnoreCase),
            new[] { new RawVmStorageRoots(Vm, VmFolder, VmFolder, VmFolder) },
            new[] { HostRoot },
            new HashSet<Guid> { Vm, Checkpoint },
            new[] { new RawCheckpointInfo(Vm, Checkpoint, "baseline", new DateTime(2026, 9, 1)) });

        private string Touch(string path, string content = "data")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private string AttachDisk(string path, string? parent = null)
        {
            Touch(path);
            _attachments.Add(new RawAttachment(Vm, null, path, AttachedMediaKind.VirtualHardDisk));
            _files[path] = new RawStorageFile(path, true, parent == null ? (ushort)3 : (ushort)4, parent, 4);
            if (parent != null && !_files.ContainsKey(parent))
            {
                Touch(parent);
                _files[parent] = new RawStorageFile(parent, true, 3, null, 4);
            }
            return path;
        }

        private Task<StorageScan> Scan() => new StorageScanner(_host.Object, _store).ScanAsync();

        private static OrphanItem? Find(StorageScan scan, string path)
            => scan.Orphans.FirstOrDefault(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase));

        [TestMethod]
        public async Task UnusedDisk_IsOrphan_ButAttachedDisksAndTheirParentsAreNot()
        {
            string templates = Path.Combine(_dir, "Templates");
            string parent = Path.Combine(templates, "base.vhdx");
            string child = AttachDisk(Path.Combine(VmFolder, "Virtual Hard Disks", "lab.vhdx"), parent);
            string stray = Touch(Path.Combine(HostRoot, "Virtual hard disks", "old.vhdx"));
            string strayInTemplates = Touch(Path.Combine(templates, "old-base.avhdx"));

            var scan = await Scan();

            Assert.IsNull(Find(scan, child));
            Assert.IsNull(Find(scan, parent), "a parent disk in use is never an orphan");
            Assert.AreEqual(OrphanKind.VirtualHardDisk, Find(scan, stray)?.Kind);
            Assert.AreEqual(OrphanKind.CheckpointDisk, Find(scan, strayInTemplates)?.Kind, "folders holding VM disks are searched too");
            CollectionAssert.Contains(scan.Roots.ToList(), templates);
        }

        [TestMethod]
        public async Task Isos_CountOnlyInHyperVAndVmFolders_SeedsAreNamed()
        {
            string downloads = Path.Combine(_dir, "Downloads");
            AttachDisk(Path.Combine(downloads, "imported.vhdx")); // makes Downloads a disk folder
            string userIso = Touch(Path.Combine(downloads, "kali.iso"));
            string seed = Touch(Path.Combine(HostRoot, "Virtual hard disks", "gone_20260101_cidata.iso"));

            var scan = await Scan();

            Assert.IsNull(Find(scan, userIso), "ISOs next to a VM's disk elsewhere are the user's media");
            StringAssert.Contains(Find(scan, seed)?.Note, "VMCreate setup disc");
        }

        [TestMethod]
        public async Task ConfigurationFiles_OfUnknownSystemsAreOrphans()
        {
            var unknown = Guid.NewGuid();
            string known = Touch(Path.Combine(VmFolder, "Virtual Machines", $"{Vm}.vmcx"));
            string knownCheckpoint = Touch(Path.Combine(VmFolder, "Snapshots", $"{Checkpoint}.vmrs"));
            string leftover = Touch(Path.Combine(HostRoot, "Virtual Machines", $"{unknown}.vmcx"));
            string notAGuid = Touch(Path.Combine(HostRoot, "Virtual Machines", "readme.vmcx"));

            var scan = await Scan();

            Assert.IsNull(Find(scan, known));
            Assert.IsNull(Find(scan, knownCheckpoint));
            Assert.IsNull(Find(scan, notAGuid));
            Assert.AreEqual(OrphanKind.VmConfiguration, Find(scan, leftover)?.Kind);
        }

        [TestMethod]
        public async Task EmptyFolders_TopmostOnly_HyperVAndRuntimeFoldersKept()
        {
            Touch(Path.Combine(VmFolder, "Virtual Machines", $"{Vm}.vmcx"));
            Directory.CreateDirectory(Path.Combine(VmFolder, "Virtual Machines", Vm.ToString())); // runtime folder, empty while off
            Directory.CreateDirectory(Path.Combine(VmFolder, "Snapshots"));                        // Hyper-V's own
            Directory.CreateDirectory(Path.Combine(HostRoot, "Snapshots"));
            string leftover = Path.Combine(HostRoot, "deleted-vm_2026");
            Directory.CreateDirectory(Path.Combine(leftover, "Virtual Machines"));
            string unknownRuntime = Path.Combine(VmFolder, "Virtual Machines", Guid.NewGuid().ToString());
            Directory.CreateDirectory(unknownRuntime);

            var empties = (await Scan()).Orphans.Where(o => o.Kind == OrphanKind.EmptyFolder).Select(o => o.Path).ToList();

            CollectionAssert.AreEquivalent(new[] { leftover, unknownRuntime }, empties);
        }

        [TestMethod]
        public async Task ForeignDisksAndManagerArchives_AreSkipped()
        {
            string wsl = Touch(Path.Combine(HostRoot, "ubuntu", "ext4.vhdx"));
            string docker = Touch(Path.Combine(HostRoot, "Docker", "wsl", "data.vhdx"));
            string archive = Path.Combine(HostRoot, "Labs_20261101-1200");
            Touch(Path.Combine(archive, "manifest.json"), "{ \"tool\": \"VMCreate Manager\" }");
            string archived = Touch(Path.Combine(archive, "lab", "Virtual Hard Disks", "lab.vhdx"));

            var scan = await Scan();

            Assert.IsNull(Find(scan, wsl));
            Assert.IsNull(Find(scan, docker));
            Assert.IsNull(Find(scan, archived));
            Assert.IsTrue(scan.Skipped.Any(s => s.Path == wsl && s.Reason.Contains("WSL")));
            Assert.IsTrue(scan.Skipped.Any(s => s.Path == archive));
        }

        [TestMethod]
        public async Task FileOpenElsewhere_IsInUse()
        {
            string mounted = Touch(Path.Combine(HostRoot, "mounted.vhdx"));
            using var hold = new FileStream(mounted, FileMode.Open, FileAccess.Read, FileShare.None);

            var item = Find(await Scan(), mounted);

            Assert.IsTrue(item?.InUse);
        }

        [TestMethod]
        public async Task DifferencingNotes_ParentAndChildren()
        {
            string parent = Touch(Path.Combine(HostRoot, "base.vhdx"));
            string child = Touch(Path.Combine(HostRoot, "child.avhdx"));
            _inspected.Add(new RawStorageFile(child, true, 4, parent, 4));

            var scan = await Scan();

            StringAssert.Contains(Find(scan, child)?.Note, "Differencing disk of " + parent);
            StringAssert.Contains(Find(scan, parent)?.Note, "Parent of 1 other unused disk(s): child.avhdx");
        }

        [TestMethod]
        public async Task DiskReferencedThroughAJunction_IsNotAnOrphan()
        {
            string real = Path.Combine(HostRoot, "real");
            string disk = Touch(Path.Combine(real, "lab2.vhdx"));
            string link = Path.Combine(_dir, "link");
            var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            mklink.WaitForExit();
            if (!Directory.Exists(link))
                Assert.Inconclusive("Could not create a junction here.");
            string viaLink = Path.Combine(link, "lab2.vhdx");
            _attachments.Add(new RawAttachment(Vm, null, viaLink, AttachedMediaKind.VirtualHardDisk));
            _files[viaLink] = new RawStorageFile(viaLink, true, 3, null, 4);

            try
            {
                Assert.IsNull(Find(await Scan(), disk));
            }
            finally
            {
                Directory.Delete(link); // removes the junction only
            }
        }

        [TestMethod]
        public void FindMissing_ReportsMissingDisksAndBrokenChains()
        {
            string disk = Path.Combine(VmFolder, "gone.vhdx");
            string child = Path.Combine(VmFolder, "child.vhdx");
            string parent = Path.Combine(VmFolder, "parent.vhdx");
            _attachments.Add(new RawAttachment(Vm, null, disk, AttachedMediaKind.VirtualHardDisk));
            _attachments.Add(new RawAttachment(Vm, "baseline", child, AttachedMediaKind.VirtualHardDisk));
            _files[disk] = new RawStorageFile(disk, false, 0, null, null);
            _files[child] = new RawStorageFile(child, true, 4, parent, 1);
            _files[parent] = new RawStorageFile(parent, false, 0, null, null);

            var missing = StorageScanner.FindMissing(Graph(), new Dictionary<Guid, string> { [Vm] = "lab" });

            Assert.AreEqual(2, missing.Count);
            Assert.AreEqual("Virtual disk missing or unreadable", missing.Single(m => m.Path == disk).Problem);
            var broken = missing.Single(m => m.Path == parent);
            Assert.AreEqual("baseline", broken.CheckpointName);
            StringAssert.Contains(broken.Problem, "Parent disk missing");
        }

        // ── cleanup ──────────────────────────────────────────────────────

        [TestMethod]
        public async Task Cleanup_DeletesOrphans_AndFoldersTheyLeaveEmpty_ButKeepsKeptFolders()
        {
            string leftoverDir = Path.Combine(HostRoot, "deleted-vm");
            string disk = Touch(Path.Combine(leftoverDir, "Virtual Hard Disks", "deleted-vm.vhdx"));
            string vmDisk = AttachDisk(Path.Combine(VmFolder, "lab.vhdx"));
            string straySibling = Touch(Path.Combine(VmFolder, "stray.vhdx"));
            string runtime = Path.Combine(VmFolder, "Virtual Machines", Vm.ToString());
            Directory.CreateDirectory(runtime);
            var scanner = new StorageScanner(_host.Object, _store);
            var remover = new DeletingRemover();
            var scan = await scanner.ScanAsync();

            var result = await new OrphanCleanupService(scanner, remover)
                .DeleteAsync(new[] { Find(scan, disk)!, Find(scan, straySibling)! }, permanently: true);

            Assert.IsTrue(result.Complete, string.Join("; ", result.FailedFiles.Select(f => f.Error)));
            CollectionAssert.AreEquivalent(new[] { disk, straySibling }, remover.Removed);
            Assert.IsFalse(Directory.Exists(leftoverDir), "the emptied leftover folder goes too");
            Assert.IsTrue(File.Exists(vmDisk));
            Assert.IsTrue(Directory.Exists(runtime), "a known VM's empty runtime folder stays");
            Assert.IsTrue(Directory.Exists(HostRoot));
        }

        [TestMethod]
        public async Task Cleanup_RefusesFilesAVmNowUses_OrThatChanged()
        {
            string reused = Touch(Path.Combine(HostRoot, "reused.vhdx"));
            string changed = Touch(Path.Combine(HostRoot, "changed.vhdx"));
            var scanner = new StorageScanner(_host.Object, _store);
            var scan = await scanner.ScanAsync();
            var approved = new[] { Find(scan, reused)!, Find(scan, changed)! };

            // Meanwhile: a VM attached one, and the other grew.
            _attachments.Add(new RawAttachment(Vm, null, reused, AttachedMediaKind.VirtualHardDisk));
            _files[reused] = new RawStorageFile(reused, true, 3, null, 4);
            File.AppendAllText(changed, "more");
            var remover = new DeletingRemover();

            var result = await new OrphanCleanupService(scanner, remover).DeleteAsync(approved, permanently: true);

            Assert.AreEqual(0, remover.Removed.Count);
            StringAssert.Contains(result.FailedFiles.Single(f => f.Path == reused).Error, "refers to it now");
            StringAssert.Contains(result.FailedFiles.Single(f => f.Path == changed).Error, "changed since the scan");
            Assert.IsTrue(File.Exists(reused) && File.Exists(changed));
        }

        [TestMethod]
        public async Task Cleanup_RemovesEmptyFolder_OnlyWhileStillEmpty()
        {
            string empty = Path.Combine(HostRoot, "old-a");
            string refilled = Path.Combine(HostRoot, "old-b");
            Directory.CreateDirectory(Path.Combine(empty, "Virtual Machines"));
            Directory.CreateDirectory(refilled);
            var scanner = new StorageScanner(_host.Object, _store);
            var scan = await scanner.ScanAsync();
            var approved = new[] { Find(scan, empty)!, Find(scan, refilled)! };
            Touch(Path.Combine(refilled, "notes.txt"));

            var result = await new OrphanCleanupService(scanner, new DeletingRemover()).DeleteAsync(approved, permanently: true);

            Assert.IsFalse(Directory.Exists(empty));
            Assert.IsTrue(File.Exists(Path.Combine(refilled, "notes.txt")));
            Assert.AreEqual(refilled, result.FailedFiles.Single().Path);
        }

        // ── report ───────────────────────────────────────────────────────

        [TestMethod]
        public async Task Report_SplitsOwnAndSharedBytes_ListsOldCheckpoints()
        {
            var clone = Guid.NewGuid();
            string parent = Path.Combine(_dir, "Templates", "base.vhdx");
            string own = AttachDisk(Path.Combine(VmFolder, "lab.vhdx"), parent);
            string cloneDisk = Path.Combine(_dir, "clone", "clone.vhdx");
            _attachments.Add(new RawAttachment(clone, null, cloneDisk, AttachedMediaKind.VirtualHardDisk));
            _files[cloneDisk] = new RawStorageFile(cloneDisk, true, 4, parent, 1);
            Touch(Path.Combine(HostRoot, "stray.vhdx"), "12345");
            var scan = await Scan();
            scan = scan with { VmNames = new Dictionary<Guid, string> { [Vm] = "lab", [clone] = "clone" } };

            var report = StorageReportBuilder.Build(scan, 30, new DateTime(2026, 11, 1), _ => (1000, 250, "Data"));

            var lab = report.Vms.Single(v => v.VmId == Vm);
            Assert.AreEqual(4, lab.OwnBytes);
            Assert.AreEqual(4, lab.SharedBytes, "the template's base disk is shared with the clone");
            Assert.AreEqual(1, lab.Checkpoints);
            Assert.AreEqual("baseline", report.OldCheckpoints.Single().Name);
            Assert.AreEqual(61, report.OldCheckpoints.Single().AgeDays);
            var volume = report.Volumes.Single();
            Assert.AreEqual(0.75, volume.UsedFraction!.Value, 1e-9);
            Assert.AreEqual(9, volume.VmBytes, "each disk once: 4 + 4 + 1");
            Assert.AreEqual(5, volume.OrphanBytes);
        }
    }
}
