using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VMCreate.Manager.HyperV;
using VMCreate.Manager.Inventory;
using VMCreate.Metadata;

namespace VMCreate.Manager.Deletion
{
    public sealed record FileFailure(string Path, string Error);

    public sealed record DeletionResult(
        IReadOnlyList<string> DeletedFiles,
        IReadOnlyList<FileFailure> FailedFiles,
        IReadOnlyList<string> RemovedFolders,
        long BytesFreed)
    {
        public bool Complete => FailedFiles.Count == 0;
    }

    /// <summary>Thrown when a VM cannot be deleted, or the host changed since the preview.</summary>
    public sealed class DeletionRefusedException : Exception
    {
        public DeletionRefusedException(string message) : base(message) { }
    }

    /// <summary>
    /// Plans and carries out "delete VM and its disks". Facts come fresh from the
    /// host and the metadata store on every call, so a preview cannot be stale
    /// by the time it is executed.
    /// </summary>
    public sealed class VmDeletionService
    {
        private readonly IHyperVHost _host;
        private readonly IVmMetadataStore _store;
        private readonly IFileRemover _remover;
        private readonly ILogger<VmDeletionService> _logger;

        public VmDeletionService(IHyperVHost host, IVmMetadataStore store, IFileRemover remover, ILogger<VmDeletionService>? logger = null)
        {
            _host = host;
            _store = store;
            _remover = remover;
            _logger = logger ?? NullLogger<VmDeletionService>.Instance;
        }

        public async Task<DeletionPlan> PlanAsync(Guid vmId, CancellationToken cancellationToken = default)
        {
            var snapshotTask = _host.ReadSnapshotAsync(cancellationToken);
            var graphTask = _host.ReadStorageGraphAsync(cancellationToken);
            var docTask = _store.LoadAsync(cancellationToken);
            await Task.WhenAll(snapshotTask, graphTask, docTask).ConfigureAwait(false);

            var snapshot = snapshotTask.Result;
            var vm = snapshot.Vms.FirstOrDefault(v => v.Id == vmId)
                     ?? throw new DeletionRefusedException("The VM no longer exists.");
            var meta = docTask.Result.FindVm(vmId);
            var target = new DeletionTarget(vmId, vm.Name, VmStateMapper.FromEnabledState(vm.EnabledState),
                meta?.Protected ?? false, meta?.IsTemplate ?? false);
            var names = snapshot.Vms.ToDictionary(v => v.Id, v => v.Name);
            return DeletionPlanner.Plan(target, graphTask.Result, names);
        }

        /// <summary>
        /// Deletes the VM as <paramref name="approved"/> showed it. Re-plans first and
        /// refuses when the VM is now blocked or would delete different files.
        /// Files that cannot be removed are reported, not fatal: the VM is gone by then.
        /// </summary>
        public async Task<DeletionResult> ExecuteAsync(DeletionPlan approved, bool permanently, CancellationToken cancellationToken = default)
        {
            var plan = await PlanAsync(approved.VmId, cancellationToken).ConfigureAwait(false);
            if (!plan.CanExecute)
                throw new DeletionRefusedException(string.Join(" ", plan.Blockers));
            if (!plan.DeletesSameAs(approved))
                throw new DeletionRefusedException("The VM's disks changed since the preview. Review the deletion again.");

            await _host.DestroyVmAsync(plan.VmId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Deleting VM {Name} ({VmId}): {Count} file(s)", plan.VmName, plan.VmId, plan.FilesToDelete.Count());

            // Past the point of no return: finish the cleanup even if cancelled.
            var result = await Task.Run(() => RemoveFiles(plan, permanently), CancellationToken.None).ConfigureAwait(false);

            try
            {
                await _store.UpdateAsync(doc => doc.Vms.RemoveAll(v => v.VmId == plan.VmId), CancellationToken.None).ConfigureAwait(false);
            }
            catch (MetadataStoreException ex)
            {
                _logger.LogWarning(ex, "VM {VmId} deleted but its metadata entry could not be removed", plan.VmId);
            }
            return result;
        }

        private DeletionResult RemoveFiles(DeletionPlan plan, bool permanently)
        {
            var deleted = new List<string>();
            var failed = new List<FileFailure>();
            long freed = 0;
            foreach (var file in plan.FilesToDelete)
            {
                try
                {
                    _remover.Remove(file.Path, permanently);
                    deleted.Add(file.Path);
                    freed += file.SizeBytes ?? 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not remove {Path}", file.Path);
                    failed.Add(new FileFailure(file.Path, ex.Message));
                }
            }

            var removed = new List<string>();
            foreach (string folder in plan.Folders) // deepest first
                RemoveEmptyTree(folder, removed);
            return new DeletionResult(deleted, failed, removed, freed);
        }

        /// <summary>Removes the folder's empty subfolders, then the folder itself if empty. Never follows links.</summary>
        private void RemoveEmptyTree(string folder, List<string> removed)
        {
            try
            {
                var info = new DirectoryInfo(folder);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return;
                foreach (var sub in info.EnumerateDirectories())
                    RemoveEmptyTree(sub.FullName, removed);
                if (!info.EnumerateFileSystemInfos().Any())
                {
                    info.Delete();
                    removed.Add(folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogInformation("Kept folder {Folder}: {Error}", folder, ex.Message);
            }
        }
    }
}
