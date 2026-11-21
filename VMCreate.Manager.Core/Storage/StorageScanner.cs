using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VMCreate.Manager.Deletion;
using VMCreate.Manager.HyperV;
using VMCreate.Metadata;

namespace VMCreate.Manager.Storage
{
    public enum OrphanKind
    {
        VirtualHardDisk,
        CheckpointDisk,
        Iso,

        /// <summary>*.vmcx / *.vmrs / *.vmgs of a VM or checkpoint Hyper-V does not know.</summary>
        VmConfiguration,

        /// <summary>A folder with no files anywhere below it.</summary>
        EmptyFolder,
    }

    /// <summary>A file (or empty folder) on the host that no VM, checkpoint or import uses.</summary>
    /// <param name="Note">Extra context: parent disk, children, VMCreate seed, …</param>
    /// <param name="InUse">Another program holds it open (a disk mounted on the host, WSL, …): never deleted.</param>
    public sealed record OrphanItem(string Path, OrphanKind Kind, long SizeBytes, DateTime LastWriteUtc, string Reason, string? Note, bool InUse);

    /// <summary>A file or folder the scan deliberately left alone.</summary>
    public sealed record SkippedPath(string Path, string Reason);

    /// <summary>A file a VM or checkpoint refers to that is not there (or Hyper-V cannot read).</summary>
    public sealed record MissingFile(Guid VmId, string VmName, string? CheckpointName, string Path, string Problem);

    /// <summary>One pass over the host's storage: the referenced set, orphans, and broken references.</summary>
    public sealed record StorageScan(
        StorageGraph Graph,
        IReadOnlyDictionary<Guid, string> VmNames,
        IReadOnlyList<string> Roots,
        IReadOnlyList<OrphanItem> Orphans,
        IReadOnlyList<SkippedPath> Skipped,
        IReadOnlyList<string> UnreadableFolders,
        IReadOnlyList<MissingFile> Missing,
        DateTimeOffset ScannedAt)
    {
        public long OrphanBytes => Orphans.Sum(o => o.SizeBytes);
    }

    /// <summary>
    /// Finds orphaned files: virtual disks, ISOs and VM configuration files under
    /// Hyper-V's default folders, every VM's folders and disk folders, and
    /// user-added folders, that nothing Hyper-V knows refers to. The referenced
    /// set is every attachment of every VM and checkpoint plus all parent disks
    /// (<see cref="StorageGraph"/>), so a template's base disk is never an orphan.
    /// </summary>
    public sealed class StorageScanner
    {
        private static readonly string[] DiskExtensions = { ".vhd", ".vhdx", ".avhd", ".avhdx" };
        private static readonly string[] ConfigurationExtensions = { ".vmcx", ".vmrs", ".vmgs" };
        private static readonly string[] SeedSuffixes = { "_cidata.iso", "_unattend.iso" };

        /// <summary>Disks other software owns, recognised by file name.</summary>
        private static readonly (string Name, string Owner)[] ForeignFileNames =
        {
            ("ext4.vhdx", "WSL"),
            ("swap.vhdx", "WSL"),
            ("docker_data.vhdx", "Docker Desktop"),
            ("DockerDesktop.vhdx", "Docker Desktop"),
        };

        /// <summary>Folders other software owns, recognised by a path segment.</summary>
        private static readonly (string Segment, string Owner)[] ForeignFolders =
        {
            (@"\Docker\", "Docker"),
            (@"\DockerDesktop\", "Docker Desktop"),
            (@"\wsl\", "WSL"),
            (@"\Packages\", "a Store app (WSL distributions live here)"),
            (@"\Microsoft\Windows\Containers\", "Windows Sandbox / containers"),
            (@"\WindowsImageBackup\", "Windows Backup"),
            (@"\$Recycle.Bin\", "the Recycle Bin"),
            (@"\System Volume Information\", "Windows"),
        };

        /// <summary>Folders Hyper-V creates inside a VM's (or its own default) folder; it expects them to stay.</summary>
        private static readonly string[] HyperVSubfolders =
        {
            "Virtual Machines", "Snapshots", "Virtual Hard Disks", "Smart Paging", "Planned Virtual Machines",
            "UndoLog Configuration", "Groups", "Resource Types",
        };

        private const int MaxDepth = 64;

        private readonly IHyperVHost _host;
        private readonly IVmMetadataStore _store;
        private readonly ILogger<StorageScanner> _logger;

        public StorageScanner(IHyperVHost host, IVmMetadataStore store, ILogger<StorageScanner>? logger = null)
        {
            _host = host;
            _store = store;
            _logger = logger ?? NullLogger<StorageScanner>.Instance;
        }

        public async Task<StorageScan> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report("Reading which files the VMs use…");
            var graphTask = _host.ReadStorageGraphAsync(cancellationToken);
            var snapshotTask = _host.ReadSnapshotAsync(cancellationToken);
            var docTask = _store.LoadAsync(cancellationToken);
            await Task.WhenAll(graphTask, snapshotTask, docTask).ConfigureAwait(false);
            var graph = graphTask.Result;
            var names = snapshotTask.Result.Vms.ToDictionary(v => v.Id, v => v.Name);

            progress?.Report("Searching the VM folders…");
            var found = await Task.Run(() => FindCandidates(graph, docTask.Result.Storage.ExtraScanFolders, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            var disks = found.Items.Where(c => c.Kind is OrphanKind.VirtualHardDisk or OrphanKind.CheckpointDisk).Select(c => c.Path).ToList();
            IReadOnlyList<RawStorageFile> inspected = Array.Empty<RawStorageFile>();
            if (disks.Count > 0)
            {
                progress?.Report($"Inspecting {disks.Count} unused virtual disk(s)…");
                try
                {
                    inspected = await _host.InspectVirtualDisksAsync(disks, cancellationToken).ConfigureAwait(false);
                }
                catch (HyperVOperationException ex)
                {
                    _logger.LogWarning(ex, "Could not inspect unused virtual disks");
                }
            }

            var orphans = await Task.Run(() => Describe(found.Items, inspected, graph), cancellationToken).ConfigureAwait(false);
            return new StorageScan(graph, names, found.Roots, orphans, found.Skipped, found.Unreadable,
                FindMissing(graph, names), DateTimeOffset.Now);
        }

        internal sealed record Candidate(string Path, OrphanKind Kind, long SizeBytes, DateTime LastWriteUtc);

        internal sealed record Candidates(
            IReadOnlyList<string> Roots,
            IReadOnlyList<Candidate> Items,
            IReadOnlyList<SkippedPath> Skipped,
            IReadOnlyList<string> Unreadable);

        /// <summary>Folders searched, and where installation media counts (not next to a VM's disk somewhere like Downloads).</summary>
        internal static (List<string> Walk, List<string> IsoRoots, List<string> Protected) ScanRoots(StorageGraph graph, IEnumerable<string> extraFolders)
        {
            var vmRoots = graph.Roots.SelectMany(r => new[] { r.ConfigurationRoot, r.SnapshotRoot, r.SwapFileRoot });
            var extra = extraFolders.Select(Normalize).OfType<string>().ToList();
            var isoRoots = graph.HostDefaultRoots.Concat(vmRoots).Select(Normalize).OfType<string>()
                .Where(p => !IsDriveRoot(p))
                .Concat(extra)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Folders holding the VMs' disks (a template's disk folder, for example).
            var diskFolders = graph.Files.Values
                .Where(f => DiskExtensions.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase))
                .Select(f => Normalize(Path.GetDirectoryName(f.Path)))
                .OfType<string>()
                .Where(p => !IsDriveRoot(p));

            var all = isoRoots.Concat(diskFolders).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToList();
            var walk = new List<string>();
            foreach (string root in all)
            {
                if (!walk.Any(w => DeletionPlanner.IsUnder(root, w)))
                    walk.Add(root);
            }

            // Never offered as empty folders, nor removed when emptied: the search roots,
            // Hyper-V's defaults and each VM's own folders.
            var protectedFolders = graph.HostDefaultRoots.Concat(vmRoots).Concat(all)
                .Select(Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return (walk, isoRoots, protectedFolders);
        }

        internal static Candidates FindCandidates(StorageGraph graph, IEnumerable<string> extraFolders, CancellationToken ct)
        {
            var (walk, isoRoots, protectedFolders) = ScanRoots(graph, extraFolders);
            var candidates = new List<Candidate>();
            var skipped = new List<SkippedPath>();
            var unreadable = new List<string>();
            var empty = new List<string>();
            var systemIds = graph.SystemIds;
            var referenced = ReferencedPaths(graph);

            // Returns true when the folder holds anything (or could not be read).
            bool Walk(DirectoryInfo dir, int depth)
            {
                ct.ThrowIfCancellationRequested();
                string path = dir.FullName;
                if (ForeignFolder(path) is string owner)
                {
                    skipped.Add(new SkippedPath(path, $"Belongs to {owner}"));
                    return true;
                }
                if (IsManagerArchive(path))
                {
                    skipped.Add(new SkippedPath(path, "An archive made by VMCreate Manager"));
                    return true;
                }
                if (depth > MaxDepth)
                {
                    skipped.Add(new SkippedPath(path, "Nested too deep"));
                    return true;
                }

                bool hasContent = false;
                try
                {
                    foreach (var file in dir.EnumerateFiles())
                    {
                        hasContent = true;
                        if (Classify(file, referenced, systemIds, isoRoots, skipped) is Candidate c)
                            candidates.Add(c);
                    }
                    foreach (var sub in dir.EnumerateDirectories())
                    {
                        if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            skipped.Add(new SkippedPath(sub.FullName, "A link to another folder; not followed"));
                            hasContent = true;
                            continue;
                        }
                        hasContent |= Walk(sub, depth + 1);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    unreadable.Add(path);
                    return true;
                }

                if (!hasContent && !IsKeptFolder(path, protectedFolders, systemIds))
                    empty.Add(path);
                return hasContent;
            }

            foreach (string root in walk)
            {
                var info = new DirectoryInfo(root);
                if (info.Exists)
                    Walk(info, 0);
            }

            // Report the topmost empty folder of each empty tree only.
            var emptySet = empty.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in empty.Where(d => Path.GetDirectoryName(d) is not string parent || !emptySet.Contains(parent)))
            {
                var info = new DirectoryInfo(dir);
                candidates.Add(new Candidate(dir, OrphanKind.EmptyFolder, 0, info.LastWriteTimeUtc));
            }
            return new Candidates(walk, candidates, skipped, unreadable);
        }

        /// <summary>
        /// Empty folders that are nevertheless in use: search roots, Hyper-V's and
        /// the VMs' own folders and anything above them, Hyper-V's standard
        /// subfolders in those, and runtime folders named after a VM or checkpoint
        /// Hyper-V knows (empty while that VM is off).
        /// </summary>
        internal static bool IsKeptFolder(string path, IReadOnlyList<string> protectedFolders, IReadOnlySet<Guid> systemIds)
        {
            if (protectedFolders.Any(p => DeletionPlanner.IsUnder(p, path)))
                return true;
            string name = Path.GetFileName(path);
            if (Guid.TryParse(name, out var id) && systemIds.Contains(id))
                return true;
            return HyperVSubfolders.Contains(name, StringComparer.OrdinalIgnoreCase)
                   && Path.GetDirectoryName(path) is string parent
                   && protectedFolders.Contains(parent, StringComparer.OrdinalIgnoreCase);
        }

        private static Candidate? Classify(FileInfo file, HashSet<string> referenced, IReadOnlySet<Guid> systemIds, List<string> isoRoots, List<SkippedPath> skipped)
        {
            string path = file.FullName;
            string ext = file.Extension;
            OrphanKind kind;
            if (DiskExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                if (referenced.Contains(path))
                    return null;
                if (ForeignFileNames.FirstOrDefault(f => string.Equals(f.Name, file.Name, StringComparison.OrdinalIgnoreCase)) is { Owner: not null } foreign)
                {
                    skipped.Add(new SkippedPath(path, $"Belongs to {foreign.Owner}"));
                    return null;
                }
                kind = ext.StartsWith(".avhd", StringComparison.OrdinalIgnoreCase) ? OrphanKind.CheckpointDisk : OrphanKind.VirtualHardDisk;
            }
            else if (string.Equals(ext, ".iso", StringComparison.OrdinalIgnoreCase))
            {
                if (referenced.Contains(path) || !isoRoots.Any(r => DeletionPlanner.IsUnder(path, r)))
                    return null;
                kind = OrphanKind.Iso;
            }
            else if (ConfigurationExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id) || systemIds.Contains(id))
                    return null;
                kind = OrphanKind.VmConfiguration;
            }
            else
            {
                return null;
            }
            // Found through a link: compare where it really lives too.
            if (kind != OrphanKind.VmConfiguration && FinalPath.TryResolve(path) is string real && referenced.Contains(real))
                return null;
            return new Candidate(path, kind, file.Length, file.LastWriteTimeUtc);
        }

        /// <summary>
        /// The paths VMs use, as Hyper-V spells them and as the file system resolves
        /// them (junctions, symbolic links, 8.3 names): a VM that refers to a disk
        /// through a link must not make the real file look unused.
        /// </summary>
        internal static HashSet<string> ReferencedPaths(StorageGraph graph)
        {
            var set = new HashSet<string>(graph.Files.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var file in graph.Files.Values.Where(f => f.Exists || File.Exists(f.Path)))
            {
                if (FinalPath.TryResolve(file.Path) is string real)
                    set.Add(real);
            }
            return set;
        }

        /// <summary>Reasons and notes; parent links from the image service; open-file check.</summary>
        internal static List<OrphanItem> Describe(IReadOnlyList<Candidate> candidates, IReadOnlyList<RawStorageFile> inspected, StorageGraph graph)
        {
            var info = inspected.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var children = inspected.Where(f => f.ParentPath != null)
                .GroupBy(f => f.ParentPath!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(f => Path.GetFileName(f.Path)).ToList(), StringComparer.OrdinalIgnoreCase);

            var items = new List<OrphanItem>(candidates.Count);
            foreach (var c in candidates.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
            {
                string reason;
                var notes = new List<string>();
                bool inUse = false;
                switch (c.Kind)
                {
                    case OrphanKind.VirtualHardDisk:
                    case OrphanKind.CheckpointDisk:
                        reason = "No VM or checkpoint uses it";
                        if (info.TryGetValue(c.Path, out var disk))
                        {
                            if (!disk.Exists)
                                notes.Add("Hyper-V cannot read it (damaged, or not a virtual disk)");
                            else if (disk.ParentPath is string parent)
                            {
                                bool parentOk = graph.Files.TryGetValue(parent, out var p) ? p.Exists : File.Exists(parent);
                                notes.Add(parentOk ? $"Differencing disk of {parent}" : $"Differencing disk; its parent is missing: {parent}");
                            }
                        }
                        if (children.TryGetValue(c.Path, out var kids))
                            notes.Add($"Parent of {kids.Count} other unused disk(s): {string.Join(", ", kids)}");
                        inUse = IsOpenElsewhere(c.Path);
                        break;
                    case OrphanKind.Iso:
                        reason = "No VM uses it";
                        if (SeedSuffixes.Any(s => c.Path.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                            notes.Add("VMCreate setup disc of a VM that no longer exists");
                        inUse = IsOpenElsewhere(c.Path);
                        break;
                    case OrphanKind.VmConfiguration:
                        reason = "Hyper-V does not know this VM or checkpoint";
                        notes.Add("Left behind by a VM removed outside Hyper-V, or part of an export that was never imported");
                        break;
                    default:
                        reason = "Empty folder";
                        break;
                }
                if (inUse)
                    notes.Insert(0, "Open in another program (mounted on the host?), so it is left alone");
                items.Add(new OrphanItem(c.Path, c.Kind, c.SizeBytes, c.LastWriteUtc, reason,
                    notes.Count == 0 ? null : string.Join(". ", notes), inUse));
            }
            return items;
        }

        /// <summary>Files VMs and checkpoints refer to that are missing; for parents, the chain is broken.</summary>
        internal static List<MissingFile> FindMissing(StorageGraph graph, IReadOnlyDictionary<Guid, string> names)
        {
            var missing = new List<MissingFile>();
            var seen = new HashSet<(Guid, string)>();
            foreach (var a in graph.Attachments)
            {
                if (a.VmId is not Guid vmId)
                    continue; // a VM being created or imported
                foreach (string path in DeletionPlanner.Chain(a.Path, graph))
                {
                    if (!graph.Files.TryGetValue(path, out var f) || f.Exists)
                        continue;
                    if (!seen.Add((vmId, path)))
                        break;
                    string problem = !string.Equals(path, a.Path, StringComparison.OrdinalIgnoreCase)
                        ? $"Parent disk missing or unreadable: {Path.GetFileName(a.Path)} cannot start"
                        : a.Kind switch
                        {
                            AttachedMediaKind.Iso => "Installation media not found",
                            AttachedMediaKind.Floppy => "Floppy image not found",
                            _ => "Virtual disk missing or unreadable",
                        };
                    missing.Add(new MissingFile(vmId, names.TryGetValue(vmId, out var n) ? n : vmId.ToString(), a.CheckpointName, path, problem));
                    break;
                }
            }
            return missing;
        }

        /// <summary>True when another process has the file open without sharing (a mounted disk, a running WSL distribution).</summary>
        internal static bool IsOpenElsewhere(string path)
        {
            try
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return false;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) // sharing / lock violation
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string? ForeignFolder(string path)
        {
            string withSlash = path.EndsWith('\\') ? path : path + '\\';
            return ForeignFolders.FirstOrDefault(f => withSlash.Contains(f.Segment, StringComparison.OrdinalIgnoreCase)).Owner;
        }

        private static bool IsManagerArchive(string dir)
        {
            string manifest = Path.Combine(dir, "manifest.json");
            try
            {
                return File.Exists(manifest) && File.ReadAllText(manifest).Contains("\"VMCreate Manager\"", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsDriveRoot(string path)
            => Path.GetPathRoot(path) is string root && string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);

        internal static string? Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
    }
}
