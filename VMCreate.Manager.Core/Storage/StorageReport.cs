using VMCreate.Manager.Deletion;
using VMCreate.Manager.HyperV;

namespace VMCreate.Manager.Storage
{
    /// <param name="VmBytes">Virtual disks VMs use on this volume (each file once).</param>
    public sealed record VolumeUsage(string Root, string? Label, long? TotalBytes, long? FreeBytes, long VmBytes, long OrphanBytes)
    {
        public double? UsedFraction => TotalBytes is > 0 && FreeBytes is long free ? 1 - (double)free / TotalBytes.Value : null;
    }

    /// <param name="OwnBytes">Disks (with parents and checkpoint disks) only this VM uses.</param>
    /// <param name="SharedBytes">Disks other VMs use too, such as a template's base disk.</param>
    public sealed record VmStorageUsage(Guid VmId, string Name, long OwnBytes, long SharedBytes, int Checkpoints, DateTime? OldestCheckpoint, int MissingFiles);

    public sealed record OldCheckpoint(Guid VmId, string VmName, string Name, DateTime CreatedAt, int AgeDays);

    public sealed record StorageReport(
        IReadOnlyList<VolumeUsage> Volumes,
        IReadOnlyList<VmStorageUsage> Vms,
        IReadOnlyList<OldCheckpoint> OldCheckpoints,
        int OldCheckpointDays,
        int OrphanCount,
        long OrphanBytes,
        int MissingCount);

    public static class StorageReportBuilder
    {
        /// <param name="drive">Total, free and label of a volume root; null when unavailable.</param>
        public static StorageReport Build(StorageScan scan, int oldCheckpointDays, DateTime now,
            Func<string, (long Total, long Free, string? Label)?> drive)
        {
            var graph = scan.Graph;

            // Which VMs use each virtual disk, through any VM or checkpoint chain.
            var users = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
            var vmFiles = new Dictionary<Guid, HashSet<string>>();
            foreach (var a in graph.Attachments.Where(a => a.Kind == AttachedMediaKind.VirtualHardDisk))
            {
                foreach (string path in DeletionPlanner.Chain(a.Path, graph))
                {
                    if (!users.TryGetValue(path, out var set))
                        users[path] = set = new HashSet<Guid>();
                    if (a.VmId is Guid id)
                    {
                        set.Add(id);
                        if (!vmFiles.TryGetValue(id, out var files))
                            vmFiles[id] = files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        files.Add(path);
                    }
                    else
                    {
                        set.Add(Guid.Empty); // a VM being created or imported
                    }
                }
            }

            long Size(string path) => graph.Files.TryGetValue(path, out var f) ? f.SizeBytes ?? 0 : 0;

            var checkpoints = graph.CheckpointList.ToLookup(c => c.VmId);
            var missing = scan.Missing.ToLookup(m => m.VmId);
            var vms = scan.VmNames
                .Select(kv =>
                {
                    var files = vmFiles.TryGetValue(kv.Key, out var f) ? f : new HashSet<string>();
                    long own = files.Where(p => users[p].Count == 1).Sum(Size);
                    long shared = files.Where(p => users[p].Count > 1).Sum(Size);
                    var cps = checkpoints[kv.Key].ToList();
                    return new VmStorageUsage(kv.Key, kv.Value, own, shared, cps.Count,
                        cps.Select(c => c.CreatedAt).Where(d => d != null).Min(),
                        missing[kv.Key].Count());
                })
                .OrderByDescending(v => v.OwnBytes)
                .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var old = graph.CheckpointList
                .Where(c => c.CreatedAt is DateTime d && (now - d).TotalDays >= oldCheckpointDays)
                .Select(c => new OldCheckpoint(c.VmId, scan.VmNames.TryGetValue(c.VmId, out var n) ? n : c.VmId.ToString(), c.Name,
                    c.CreatedAt!.Value, (int)(now - c.CreatedAt.Value).TotalDays))
                .OrderByDescending(c => c.AgeDays)
                .ToList();

            var vmBytesByRoot = users.Keys
                .Where(p => graph.Files.TryGetValue(p, out var f) && f.Exists)
                .GroupBy(VolumeRoot, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(Size), StringComparer.OrdinalIgnoreCase);
            var orphanBytesByRoot = scan.Orphans
                .GroupBy(o => VolumeRoot(o.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(o => o.SizeBytes), StringComparer.OrdinalIgnoreCase);
            var volumes = vmBytesByRoot.Keys.Concat(orphanBytesByRoot.Keys)
                .Where(r => r.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .Select(r =>
                {
                    var info = drive(r);
                    return new VolumeUsage(r, info?.Label, info?.Total, info?.Free,
                        vmBytesByRoot.GetValueOrDefault(r), orphanBytesByRoot.GetValueOrDefault(r));
                })
                .ToList();

            return new StorageReport(volumes, vms, old, oldCheckpointDays, scan.Orphans.Count, scan.OrphanBytes, scan.Missing.Count);
        }

        /// <summary>Total/free/label from <see cref="DriveInfo"/>; null for a disconnected or missing drive.</summary>
        public static (long Total, long Free, string? Label)? ReadDrive(string root)
        {
            try
            {
                var info = new DriveInfo(root);
                return info.IsReady ? (info.TotalSize, info.AvailableFreeSpace, info.VolumeLabel) : null;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string VolumeRoot(string path) => Path.GetPathRoot(path) ?? "";
    }
}
