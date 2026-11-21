using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VMCreate.Manager.Deletion;

namespace VMCreate.Manager.Storage
{
    /// <summary>
    /// Deletes orphaned files the user picked from a scan. Scans again first and
    /// only removes what is still orphaned and unchanged, so a VM that started
    /// using a file since the list was shown keeps it.
    /// </summary>
    public sealed class OrphanCleanupService
    {
        private readonly StorageScanner _scanner;
        private readonly IFileRemover _remover;
        private readonly ILogger<OrphanCleanupService> _logger;

        public OrphanCleanupService(StorageScanner scanner, IFileRemover remover, ILogger<OrphanCleanupService>? logger = null)
        {
            _scanner = scanner;
            _remover = remover;
            _logger = logger ?? NullLogger<OrphanCleanupService>.Instance;
        }

        public async Task<DeletionResult> DeleteAsync(IReadOnlyCollection<OrphanItem> approved, bool permanently, CancellationToken cancellationToken = default)
        {
            var fresh = await _scanner.ScanAsync(null, cancellationToken).ConfigureAwait(false);
            return await Task.Run(() => Delete(approved, fresh, permanently), CancellationToken.None).ConfigureAwait(false);
        }

        internal DeletionResult Delete(IReadOnlyCollection<OrphanItem> approved, StorageScan fresh, bool permanently)
        {
            var current = fresh.Orphans.ToDictionary(o => o.Path, StringComparer.OrdinalIgnoreCase);
            var keepRoots = KeptFolders(fresh);
            bool IsKept(string folder) => StorageScanner.IsKeptFolder(folder, keepRoots, fresh.Graph.SystemIds);
            var deleted = new List<string>();
            var failed = new List<FileFailure>();
            var removedFolders = new List<string>();
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long freed = 0;

            // Files first, so folders they leave empty can go too.
            foreach (var item in approved.OrderBy(i => i.Kind == OrphanKind.EmptyFolder))
            {
                if (Refusal(item, current) is string why)
                {
                    failed.Add(new FileFailure(item.Path, why));
                    continue;
                }
                try
                {
                    if (item.Kind == OrphanKind.EmptyFolder)
                    {
                        RemoveEmptyTree(item.Path, removedFolders, IsKept);
                        if (Directory.Exists(item.Path))
                            failed.Add(new FileFailure(item.Path, "The folder is no longer empty."));
                    }
                    else
                    {
                        _remover.Remove(item.Path, permanently);
                        deleted.Add(item.Path);
                        freed += item.SizeBytes;
                        if (Path.GetDirectoryName(item.Path) is string dir)
                            touched.Add(dir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not remove {Path}", item.Path);
                    failed.Add(new FileFailure(item.Path, ex.Message));
                }
            }

            // Folders the deleted files leave empty, up to (not including) a kept folder.
            foreach (string dir in touched.OrderByDescending(d => d.Length))
            {
                for (string? d = dir; d != null && !IsKept(d); d = Path.GetDirectoryName(d))
                {
                    if (!RemoveEmptyTree(d, removedFolders, IsKept))
                        break;
                }
            }
            _logger.LogInformation("Orphan cleanup: {Deleted} file(s), {Folders} folder(s) removed, {Failed} refused or failed",
                deleted.Count, removedFolders.Count, failed.Count);
            return new DeletionResult(deleted, failed, removedFolders, freed);
        }

        /// <summary>Why <paramref name="item"/> must stay, judged against a fresh scan; null when it may go.</summary>
        internal static string? Refusal(OrphanItem item, IReadOnlyDictionary<string, OrphanItem> current)
        {
            if (!current.TryGetValue(item.Path, out var now))
                return File.Exists(item.Path) || Directory.Exists(item.Path)
                    ? "No longer unused: a VM or checkpoint refers to it now."
                    : "It is gone already.";
            if (now.Kind != item.Kind || now.SizeBytes != item.SizeBytes || now.LastWriteUtc != item.LastWriteUtc)
                return "It changed since the scan. Scan again and review it.";
            if (now.InUse)
                return "Another program has it open.";
            return null;
        }

        private static List<string> KeptFolders(StorageScan scan)
            => StorageScanner.ScanRoots(scan.Graph, scan.Roots).Protected;

        /// <summary>
        /// Removes the folder when nothing but empty folders is inside; never follows
        /// links, and a kept folder inside counts as content. True when removed.
        /// </summary>
        private bool RemoveEmptyTree(string folder, List<string> removed, Func<string, bool> isKept)
        {
            try
            {
                var info = new DirectoryInfo(folder);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;
                foreach (var sub in info.EnumerateDirectories())
                {
                    if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint) && !isKept(sub.FullName))
                        RemoveEmptyTree(sub.FullName, removed, isKept);
                }
                if (info.EnumerateFileSystemInfos().Any())
                    return false;
                info.Delete();
                removed.Add(folder);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogInformation("Kept folder {Folder}: {Error}", folder, ex.Message);
                return false;
            }
        }
    }
}
