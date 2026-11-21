using VMCreate.Manager.HyperV;
using VMCreate.Manager.Inventory;

namespace VMCreate.Manager.Deletion
{
    /// <summary>The VM to delete, as the planner needs to see it.</summary>
    public sealed record DeletionTarget(Guid Id, string Name, VmState State, bool Protected, bool IsTemplate);

    public enum PlannedFileKind
    {
        VirtualHardDisk,
        /// <summary>A checkpoint's differencing disk (.avhdx).</summary>
        CheckpointDisk,
        Iso,
        Floppy,
    }

    public enum PlannedAction
    {
        Delete,
        Keep,
        /// <summary>Referenced but not on disk: nothing to delete.</summary>
        Missing,
    }

    public sealed record PlannedFile(string Path, PlannedFileKind Kind, long? SizeBytes, PlannedAction Action, string Reason);

    /// <summary>
    /// What deleting one VM would do. The preview shows exactly this, and
    /// execution re-plans and refuses to run when the result differs.
    /// </summary>
    /// <param name="Blockers">Reasons the VM cannot be deleted now; empty when it can.</param>
    /// <param name="Folders">The VM's own folders, removed afterwards when nothing is left in them.</param>
    public sealed record DeletionPlan(
        Guid VmId,
        string VmName,
        IReadOnlyList<string> Blockers,
        IReadOnlyList<PlannedFile> Files,
        IReadOnlyList<string> Folders)
    {
        public bool CanExecute => Blockers.Count == 0;

        public IEnumerable<PlannedFile> FilesToDelete => Files.Where(f => f.Action == PlannedAction.Delete);

        public long BytesToDelete => FilesToDelete.Sum(f => f.SizeBytes ?? 0);

        /// <summary>True when both plans delete the same files of the same VM.</summary>
        public bool DeletesSameAs(DeletionPlan other)
            => VmId == other.VmId
               && FilesToDelete.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .SetEquals(other.FilesToDelete.Select(f => f.Path));
    }

    /// <summary>
    /// Decides which files deleting a VM removes. Pure: everything comes from the
    /// <see cref="StorageGraph"/>, so the rules are tested on fixture graphs.
    /// <list type="bullet">
    /// <item>Virtual disks (and their parents) go only when no other VM, checkpoint
    /// or half-created system uses them, anywhere in its chain.</item>
    /// <item>ISOs and floppies go only when unused elsewhere and either inside the
    /// VM's own folders or a VMCreate seed named after the VM; installation media
    /// in Downloads or the gallery cache is never touched.</item>
    /// <item>Folders are the VM's own only: never Hyper-V's default folders, a drive
    /// root, or a folder holding another VM.</item>
    /// </list>
    /// </summary>
    public static class DeletionPlanner
    {
        private const int MaxChainDepth = 128;

        /// <summary>Seed ISOs VMCreate writes to the shared disk folder: "{vm}_cidata.iso", "{vm}_unattend.iso".</summary>
        private static readonly string[] VmCreateSeedSuffixes = { "_cidata.iso", "_unattend.iso" };

        public static DeletionPlan Plan(DeletionTarget target, StorageGraph graph, IReadOnlyDictionary<Guid, string> vmNames)
        {
            var blockers = new List<string>();
            if (target.Protected)
                blockers.Add("It is delete-protected. Remove the protection first.");
            if (target.State is not (VmState.Off or VmState.Saved))
                blockers.Add($"It is {target.State.ToString().ToLowerInvariant()}. Turn it off first.");

            // Who else uses each file, through any link of their chains.
            var users = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in graph.Attachments.Where(a => a.VmId != target.Id))
            {
                string who = a.VmId is Guid id
                    ? vmNames.TryGetValue(id, out var name) ? name : id.ToString()
                    : "a VM that is being created or imported";
                foreach (string path in Chain(a.Path, graph))
                {
                    if (!users.TryGetValue(path, out var set))
                        users[path] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(who);
                }
            }

            // The target's files: each attachment's chain, current configuration first.
            var own = new List<(string Path, AttachedMediaKind Kind)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in graph.Attachments.Where(a => a.VmId == target.Id).OrderBy(a => a.CheckpointName != null))
            {
                foreach (string path in Chain(a.Path, graph))
                {
                    if (seen.Add(path))
                        own.Add((path, a.Kind));
                }
            }

            var dependents = own.Where(f => f.Kind == AttachedMediaKind.VirtualHardDisk && users.ContainsKey(f.Path))
                .SelectMany(f => users[f.Path]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (target.IsTemplate && dependents.Count > 0)
                blockers.Add($"It is the template of {dependents.Count} linked clone(s): {string.Join(", ", dependents)}. Delete those first.");

            var unsafeFolders = UnsafeFolders(target.Id, graph);
            var files = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
            var attached = graph.Attachments.Where(a => a.VmId == target.Id).Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, kind) in own.Where(f => f.Kind == AttachedMediaKind.VirtualHardDisk))
                files[path] = PlanFile(path, kind, graph, users, () => (PlannedAction.Delete,
                    attached.Contains(path) ? "Only used by this VM" : "Parent disk that no other VM uses"));

            var folders = OwnFolders(target.Id, graph, files.Values.Where(f => f.Action == PlannedAction.Delete), unsafeFolders);
            foreach (var (path, kind) in own.Where(f => f.Kind != AttachedMediaKind.VirtualHardDisk))
            {
                files[path] = PlanFile(path, kind, graph, users, () =>
                    folders.Any(dir => IsUnder(path, dir)) ? (PlannedAction.Delete, "In this VM's folder")
                    : IsVmCreateSeed(path, target.Name) ? (PlannedAction.Delete, "VMCreate setup disc for this VM")
                    : (PlannedAction.Keep, "Installation media outside this VM's folders"));
            }

            // Keep the chain order (current disk, its parents, then checkpoints' media).
            var ordered = own.Select(f => files[f.Path]).ToList();
            return new DeletionPlan(target.Id, target.Name, blockers, ordered, folders);
        }

        private static PlannedFile PlanFile(string path, AttachedMediaKind attachedAs, StorageGraph graph,
            Dictionary<string, SortedSet<string>> users, Func<(PlannedAction, string)> whenUnshared)
        {
            graph.Files.TryGetValue(path, out var info);
            var kind = attachedAs switch
            {
                AttachedMediaKind.Iso => PlannedFileKind.Iso,
                AttachedMediaKind.Floppy => PlannedFileKind.Floppy,
                _ => Path.GetExtension(path).StartsWith(".avhd", StringComparison.OrdinalIgnoreCase)
                    ? PlannedFileKind.CheckpointDisk
                    : PlannedFileKind.VirtualHardDisk,
            };
            var (action, reason) = info is not { Exists: true } ? (PlannedAction.Missing, "File not found")
                : users.TryGetValue(path, out var who) ? (PlannedAction.Keep, $"Also used by {string.Join(", ", who)}")
                : whenUnshared();
            return new PlannedFile(path, kind, info?.SizeBytes, action, reason);
        }

        /// <summary>The file and its parents, nearest first. Stops at a missing disk or a loop.</summary>
        internal static IEnumerable<string> Chain(string path, StorageGraph graph)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? current = path;
            while (current != null && seen.Count < MaxChainDepth && seen.Add(current))
            {
                yield return current;
                current = graph.Files.TryGetValue(current, out var file) ? file.ParentPath : null;
            }
        }

        /// <summary>
        /// Folders that must survive: drive roots, Hyper-V's defaults, and every
        /// folder holding (or above) another VM's configuration.
        /// </summary>
        private static List<string> UnsafeFolders(Guid targetId, StorageGraph graph)
            => graph.HostDefaultRoots
                .Concat(graph.Roots.Where(r => r.VmId != targetId)
                    .SelectMany(r => new[] { r.ConfigurationRoot, r.SnapshotRoot, r.SwapFileRoot }))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Trim(p!))
                .ToList();

        private static List<string> OwnFolders(Guid targetId, StorageGraph graph, IEnumerable<PlannedFile> deleted, List<string> unsafeFolders)
        {
            var roots = graph.Roots.Where(r => r.VmId == targetId)
                .SelectMany(r => new[] { r.ConfigurationRoot, r.SnapshotRoot, r.SwapFileRoot });
            var diskFolders = deleted.Select(f => Path.GetDirectoryName(f.Path));
            return roots.Concat(diskFolders)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Trim(p!))
                .Where(dir => Path.GetPathRoot(dir) is string root && !string.Equals(Trim(root), dir, StringComparison.OrdinalIgnoreCase))
                // Equal to, or above, a folder that must survive: not ours to remove.
                .Where(dir => !unsafeFolders.Any(u => IsUnder(u, dir)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(dir => dir.Length)
                .ToList();
        }

        private static bool IsVmCreateSeed(string path, string vmName)
            => VmCreateSeedSuffixes.Any(s => string.Equals(Path.GetFileName(path), vmName + s, StringComparison.OrdinalIgnoreCase));

        /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
        internal static bool IsUnder(string path, string folder)
        {
            path = Trim(path);
            folder = Trim(folder);
            // A drive root keeps its separator ("D:\").
            string prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
            return string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
    }
}
