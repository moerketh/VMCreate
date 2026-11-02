using VMCreate.Manager.Deletion;
using VMCreate.Manager.HyperV;
using VMCreate.Manager.Inventory;

namespace VMCreate.Manager.Tests.Deletion
{
    [TestClass]
    public class DeletionPlannerTests
    {
        private const string HostRoot = @"D:\Hyper-V";
        private const string DiskRoot = @"D:\Hyper-V\Virtual hard disks";

        private static readonly Guid Target = Guid.NewGuid();
        private static readonly Guid Other = Guid.NewGuid();

        /// <summary>Builds a graph from attachments and (child → parent) disk links; every file exists unless listed in <c>missing</c>.</summary>
        private sealed class GraphBuilder
        {
            private readonly List<RawAttachment> _attachments = new();
            private readonly Dictionary<string, RawStorageFile> _files = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<RawVmStorageRoots> _roots = new();

            public GraphBuilder Attach(Guid? vm, string path, AttachedMediaKind kind = AttachedMediaKind.VirtualHardDisk, string? checkpoint = null, string? parent = null, bool exists = true)
            {
                _attachments.Add(new RawAttachment(vm, checkpoint, path, kind));
                return File(path, parent, exists);
            }

            public GraphBuilder File(string path, string? parent = null, bool exists = true)
            {
                _files[path] = new RawStorageFile(path, exists, (ushort)(parent == null ? 3 : 4), parent, 1024);
                return this;
            }

            public GraphBuilder Root(Guid vm, string config)
            {
                _roots.Add(new RawVmStorageRoots(vm, config, config, config));
                return this;
            }

            public StorageGraph Build() => new(_attachments, _files, _roots, new[] { HostRoot, DiskRoot });
        }

        private static DeletionPlan Plan(GraphBuilder graph, VmState state = VmState.Off, bool isProtected = false, bool template = false, string name = "target")
            => DeletionPlanner.Plan(new DeletionTarget(Target, name, state, isProtected, template), graph.Build(),
                new Dictionary<Guid, string> { [Target] = name, [Other] = "other" });

        private static PlannedFile F(DeletionPlan plan, string path) => plan.Files.Single(f => f.Path == path);

        [TestMethod]
        public void OwnDiskAndFolders_AreDeleted()
        {
            var plan = Plan(new GraphBuilder()
                .Root(Target, @"D:\Hyper-V\target")
                .Attach(Target, @"D:\Hyper-V\Virtual hard disks\target\target.vhdx"));

            Assert.IsTrue(plan.CanExecute);
            Assert.AreEqual(PlannedAction.Delete, F(plan, @"D:\Hyper-V\Virtual hard disks\target\target.vhdx").Action);
            CollectionAssert.AreEquivalent(new[] { @"D:\Hyper-V\target", @"D:\Hyper-V\Virtual hard disks\target" }, plan.Folders.ToList());
            Assert.AreEqual(1024, plan.BytesToDelete);
        }

        [TestMethod]
        public void Blockers_ProtectedRunningTemplate()
        {
            var graph = new GraphBuilder().Attach(Target, @"D:\t.vhdx");
            Assert.IsFalse(Plan(graph, isProtected: true).CanExecute);
            Assert.IsFalse(Plan(graph, state: VmState.Running).CanExecute);
            Assert.IsFalse(Plan(graph, state: VmState.Paused).CanExecute);
            Assert.IsTrue(Plan(graph, state: VmState.Saved).CanExecute);
            // A template without clones may go; its own protection is what guards it.
            Assert.IsTrue(Plan(graph, template: true).CanExecute);
        }

        [TestMethod]
        public void TemplateWithClones_IsBlocked_AndItsDiskKept()
        {
            const string parent = @"D:\Hyper-V\Virtual hard disks\tmpl\tmpl.vhdx";
            var plan = Plan(new GraphBuilder()
                .Attach(Target, parent)
                .Attach(Other, @"D:\Hyper-V\clone\clone.vhdx", parent: parent), template: true);

            Assert.IsFalse(plan.CanExecute);
            StringAssert.Contains(plan.Blockers.Single(), "other");
            Assert.AreEqual(PlannedAction.Keep, F(plan, parent).Action);
        }

        [TestMethod]
        public void Clone_DeletesItsDifferencingDisk_ButNotTheSharedParent()
        {
            const string parent = @"D:\Hyper-V\Virtual hard disks\tmpl\tmpl.vhdx";
            const string mine = @"D:\Hyper-V\target\Virtual Hard Disks\target.vhdx";
            var plan = Plan(new GraphBuilder()
                .Attach(Other, parent)
                .Attach(Target, mine, parent: parent));

            Assert.IsTrue(plan.CanExecute);
            Assert.AreEqual(PlannedAction.Delete, F(plan, mine).Action);
            var kept = F(plan, parent);
            Assert.AreEqual(PlannedAction.Keep, kept.Action);
            StringAssert.Contains(kept.Reason, "other");
            CollectionAssert.DoesNotContain(plan.Folders.ToList(), @"D:\Hyper-V\Virtual hard disks\tmpl");
        }

        [TestMethod]
        public void ParentUsedDeepInAnotherChain_IsKept()
        {
            // other: c2 → c1 → base; target uses base directly.
            var plan = Plan(new GraphBuilder()
                .File(@"D:\x\c1.vhdx", parent: @"D:\x\base.vhdx")
                .Attach(Other, @"D:\x\c2.vhdx", parent: @"D:\x\c1.vhdx")
                .Attach(Target, @"D:\x\base.vhdx"));
            Assert.AreEqual(PlannedAction.Keep, F(plan, @"D:\x\base.vhdx").Action);
        }

        [TestMethod]
        public void CheckpointChain_IsDeletedWholly()
        {
            const string baseDisk = @"D:\Hyper-V\Virtual hard disks\t\t.vhdx";
            const string current = @"D:\Hyper-V\Virtual hard disks\t\t_A.avhdx";
            const string older = @"D:\Hyper-V\Virtual hard disks\t\t_B.avhdx";
            var plan = Plan(new GraphBuilder()
                .File(older, parent: baseDisk)
                .Attach(Target, current, parent: older)
                .Attach(Target, older, checkpoint: "cp1", parent: baseDisk)
                .File(baseDisk));

            CollectionAssert.AreEqual(new[] { current, older, baseDisk }, plan.Files.Select(f => f.Path).ToList());
            Assert.IsTrue(plan.Files.All(f => f.Action == PlannedAction.Delete));
            Assert.AreEqual(PlannedFileKind.CheckpointDisk, F(plan, current).Kind);
            Assert.AreEqual(PlannedFileKind.VirtualHardDisk, F(plan, baseDisk).Kind);
        }

        [TestMethod]
        public void UnknownOwner_KeepsTheFile()
        {
            const string disk = @"D:\x\t.vhdx";
            var plan = Plan(new GraphBuilder().Attach(Target, disk).Attach(null, disk));
            Assert.AreEqual(PlannedAction.Keep, F(plan, disk).Action);
        }

        [TestMethod]
        public void OtherCheckpoint_KeepsTheFile()
        {
            const string disk = @"D:\x\t.vhdx";
            var plan = Plan(new GraphBuilder().Attach(Target, disk).Attach(Other, @"D:\x\o.avhdx", checkpoint: "cp", parent: disk));
            Assert.AreEqual(PlannedAction.Keep, F(plan, disk).Action);
        }

        [TestMethod]
        public void MissingDisk_IsReportedNotDeleted()
        {
            var plan = Plan(new GraphBuilder().Attach(Target, @"F:\gone.vhdx", exists: false));
            Assert.IsTrue(plan.CanExecute);
            Assert.AreEqual(PlannedAction.Missing, plan.Files.Single().Action);
            Assert.AreEqual(0, plan.FilesToDelete.Count());
        }

        [TestMethod]
        public void Iso_KeptOutsideTheVmFolders_DeletedInside()
        {
            const string downloads = @"C:\Users\me\Downloads\kali.iso";
            const string inside = @"D:\Hyper-V\target\seed.iso";
            var plan = Plan(new GraphBuilder()
                .Root(Target, @"D:\Hyper-V\target")
                .Attach(Target, @"D:\Hyper-V\target\t.vhdx")
                .Attach(Target, downloads, AttachedMediaKind.Iso)
                .Attach(Target, inside, AttachedMediaKind.Iso));

            Assert.AreEqual(PlannedAction.Keep, F(plan, downloads).Action);
            Assert.AreEqual(PlannedAction.Delete, F(plan, inside).Action);
            Assert.AreEqual(PlannedFileKind.Iso, F(plan, inside).Kind);
        }

        [TestMethod]
        public void VmCreateSeedIso_InTheSharedDiskFolder_IsDeleted_UnlessShared()
        {
            const string seed = @"D:\Hyper-V\Virtual hard disks\kali-01_cidata.iso";
            const string otherSeed = @"D:\Hyper-V\Virtual hard disks\kali-02_cidata.iso";
            var graph = new GraphBuilder()
                .Attach(Target, @"D:\Hyper-V\Virtual hard disks\kali-01.vhdx")
                .Attach(Target, seed, AttachedMediaKind.Iso)
                .Attach(Target, otherSeed, AttachedMediaKind.Iso);
            var plan = Plan(graph, name: "kali-01");
            Assert.AreEqual(PlannedAction.Delete, F(plan, seed).Action);
            Assert.AreEqual(PlannedAction.Keep, F(plan, otherSeed).Action);

            plan = Plan(graph.Attach(Other, seed, AttachedMediaKind.Iso), name: "kali-01");
            Assert.AreEqual(PlannedAction.Keep, F(plan, seed).Action);
        }

        [TestMethod]
        public void SharedFolders_AreNeverPlannedForRemoval()
        {
            var plan = Plan(new GraphBuilder()
                .Root(Target, HostRoot)                          // config in Hyper-V's default folder
                .Root(Other, @"D:\VMs\other")
                .Attach(Target, @"D:\Hyper-V\Virtual hard disks\t.vhdx") // disk directly in the default disk folder
                .Attach(Target, @"D:\VMs\t.vhdx")                 // above another VM's folder
                .Attach(Target, @"E:\t.vhdx"));                   // drive root

            Assert.AreEqual(3, plan.FilesToDelete.Count());
            Assert.AreEqual(0, plan.Folders.Count, string.Join(", ", plan.Folders));
        }

        [TestMethod]
        public void Folders_AreDeepestFirst()
        {
            var plan = Plan(new GraphBuilder()
                .Root(Target, @"D:\Hyper-V\t")
                .Attach(Target, @"D:\Hyper-V\t\Virtual Hard Disks\t.vhdx"));
            CollectionAssert.AreEqual(new[] { @"D:\Hyper-V\t\Virtual Hard Disks", @"D:\Hyper-V\t" }, plan.Folders.ToList());
        }

        [TestMethod]
        public void ParentLoop_DoesNotHang()
        {
            var plan = Plan(new GraphBuilder()
                .File(@"D:\x\b.vhdx", parent: @"D:\x\a.vhdx")
                .Attach(Target, @"D:\x\a.vhdx", parent: @"D:\x\b.vhdx"));
            Assert.AreEqual(2, plan.Files.Count);
        }

        [TestMethod]
        public void DeletesSameAs_ComparesTheDeletedFiles()
        {
            var a = Plan(new GraphBuilder().Attach(Target, @"D:\x\t.vhdx"));
            var b = Plan(new GraphBuilder().Attach(Target, @"d:\X\T.vhdx"));
            var c = Plan(new GraphBuilder().Attach(Target, @"D:\x\t.vhdx").Attach(Target, @"D:\x\u.vhdx"));
            var d = Plan(new GraphBuilder().Attach(Target, @"D:\x\t.vhdx").Attach(Other, @"D:\x\t.vhdx"));
            Assert.IsTrue(a.DeletesSameAs(b));
            Assert.IsFalse(a.DeletesSameAs(c));
            Assert.IsFalse(a.DeletesSameAs(d));
        }

        [TestMethod]
        public void ParseVirtualDiskSettings_ReadsTypeAndParent()
        {
            const string xml = "<INSTANCE CLASSNAME=\"Msvm_VirtualHardDiskSettingData\">"
                + "<PROPERTY NAME=\"ParentPath\" TYPE=\"string\"><VALUE>D:\\a &amp; b\\p.vhdx</VALUE></PROPERTY>"
                + "<PROPERTY NAME=\"Type\" TYPE=\"uint16\"><VALUE>4</VALUE></PROPERTY></INSTANCE>";
            Assert.AreEqual(((ushort)4, @"D:\a & b\p.vhdx"), WmiHyperVHost.ParseVirtualDiskSettings(xml));
            Assert.AreEqual(((ushort)3, (string?)null), WmiHyperVHost.ParseVirtualDiskSettings(
                "<PROPERTY NAME=\"ParentPath\" TYPE=\"string\"></PROPERTY><PROPERTY NAME=\"Type\" TYPE=\"uint16\"><VALUE>3</VALUE></PROPERTY>"));
        }
    }
}
