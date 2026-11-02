using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VMCreate.Manager.HyperV;
using VMCreate.Manager.Inventory;
using VMCreate.Metadata;

namespace VMCreate.Manager.Metadata
{
    /// <summary>A VM whose metadata was not simply in sync, and what was done about it.</summary>
    public sealed record VmSyncChange(Guid VmId, string VmName, NotesReconcileResult Result, string? PushError = null);

    /// <summary>
    /// Keeps the local metadata store and the [vmcreate] block in each VM's
    /// Notes in step: the store is the source of truth, Notes is the portable
    /// copy that travels with the VM.
    /// </summary>
    public sealed class VmMetadataSync
    {
        private readonly IVmMetadataStore _store;
        private readonly IHyperVHost _host;
        private readonly ICredentialProtector _protector;
        private readonly ILogger<VmMetadataSync> _logger;

        public VmMetadataSync(IVmMetadataStore store, IHyperVHost host, ICredentialProtector protector, ILogger<VmMetadataSync>? logger = null)
        {
            _store = store;
            _host = host;
            _protector = protector;
            _logger = logger ?? NullLogger<VmMetadataSync>.Instance;
        }

        /// <summary>
        /// Brings store and Notes in step for every VM (<see cref="NotesReconciler"/>):
        /// adopts blocks of VMs the store doesn't know, takes Notes edits made outside
        /// VMCreate tools, pushes store changes Notes hasn't caught up with, and
        /// records each VM's name and the day it was seen. The store is only written
        /// when something changed. Returns everything that was not simply in sync.
        /// </summary>
        public async Task<IReadOnlyList<VmSyncChange>> ReconcileAsync(IReadOnlyCollection<VmSummary> vms, CancellationToken cancellationToken = default)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var parsed = vms.Select(vm => (vm.Id, vm.Name, NotesBlock.Parse(vm.Notes).Payload)).ToList();

            List<VmSyncChange> Run(MetadataDocument doc, out bool dirty)
            {
                dirty = false;
                var changes = new List<VmSyncChange>();
                foreach (var (id, name, payload) in parsed)
                {
                    string? before = doc.FindVm(id)?.NotesFingerprint;
                    var result = NotesReconciler.Reconcile(doc, id, payload);
                    var entry = doc.FindVm(id);
                    if (entry != null && (NotesReconciler.MarkSeen(entry, name, today) || entry.NotesFingerprint != before))
                        dirty = true;
                    if (result.Action != NotesSyncAction.InSync)
                    {
                        dirty = true;
                        changes.Add(new VmSyncChange(id, name, result));
                    }
                }
                return changes;
            }

            // Dry run on a loaded copy first: most refreshes change nothing and must not rewrite the file.
            Run(await _store.LoadAsync(cancellationToken).ConfigureAwait(false), out bool needed);
            if (!needed)
                return Array.Empty<VmSyncChange>();

            var applied = await _store.UpdateAsync(doc => Run(doc, out _), cancellationToken).ConfigureAwait(false);
            var report = new List<VmSyncChange>(applied.Count);
            foreach (var change in applied)
            {
                string? error = null;
                if (change.Result.NeedsPush)
                {
                    try
                    {
                        await PushToNotesAsync(change.VmId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (HyperVOperationException ex)
                    {
                        _logger.LogWarning(ex, "Could not update notes of VM {VmId}", change.VmId);
                        error = ex.Message;
                    }
                }
                report.Add(change with { PushError = error });
            }
            return report;
        }

        /// <summary>Applies <paramref name="mutate"/> to the VM's entry, then mirrors the result into its Notes.</summary>
        public async Task UpdateVmAsync(Guid vmId, Action<MetadataDocument, VmMetadata> mutate, CancellationToken cancellationToken = default)
        {
            await _store.UpdateAsync(doc =>
            {
                mutate(doc, doc.GetOrAddVm(vmId));
                return true;
            }, cancellationToken).ConfigureAwait(false);
            await PushToNotesAsync(vmId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Rewrites the VM's Notes block from the store, preserving the user's
        /// free text. Skips the write when nothing changed.
        /// </summary>
        public async Task PushToNotesAsync(Guid vmId, CancellationToken cancellationToken = default)
        {
            var doc = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var vm = doc.FindVm(vmId);
            var payload = vm == null ? null : doc.ToNotesPayload(vm);
            string current = await _host.GetNotesAsync(vmId, cancellationToken).ConfigureAwait(false);
            string updated = NotesBlock.Update(current, payload);
            if (updated != current)
                await _host.SetNotesAsync(vmId, updated, cancellationToken).ConfigureAwait(false);
            if (vm != null)
                await RecordFingerprintAsync(vmId, payload, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Only after Notes holds the block: a fingerprint of a failed write would read as an outside edit.</summary>
        private Task<bool> RecordFingerprintAsync(Guid vmId, NotesPayload? written, CancellationToken cancellationToken)
        {
            string print = NotesBlock.Fingerprint(written);
            return _store.UpdateAsync(doc =>
            {
                if (doc.FindVm(vmId) is { } entry)
                    entry.NotesFingerprint = print;
                return true;
            }, cancellationToken);
        }

        /// <summary>Mirrors every VM in the folder tree into Notes, e.g. after a folder rename.</summary>
        public async Task PushAllToNotesAsync(IEnumerable<Guid> vmIds, CancellationToken cancellationToken = default)
        {
            foreach (var id in vmIds)
            {
                try
                {
                    await PushToNotesAsync(id, cancellationToken).ConfigureAwait(false);
                }
                catch (HyperVOperationException ex)
                {
                    // A VM deleted meanwhile must not stop the others from syncing.
                    _logger.LogWarning(ex, "Could not update notes of VM {VmId}", id);
                }
            }
        }

        /// <summary>True when the VM's Notes still hold plaintext credentials written by older VMCreate builds.</summary>
        public static bool HasLegacyCredentials(VmSummary vm)
            => LegacyNotesCredentials.TryExtract(NotesBlock.Parse(vm.Notes).FreeText, out _, out _, out _);

        /// <summary>
        /// Moves plaintext "Initial Username/Password" lines from Notes into the
        /// store (password DPAPI-encrypted) and removes them from Notes.
        /// Returns false when the VM has no legacy credentials.
        /// </summary>
        public async Task<bool> MigrateLegacyCredentialsAsync(Guid vmId, CancellationToken cancellationToken = default)
        {
            string current = await _host.GetNotesAsync(vmId, cancellationToken).ConfigureAwait(false);
            var (freeText, _) = NotesBlock.Parse(current);
            if (!LegacyNotesCredentials.TryExtract(freeText, out var user, out var password, out var remaining))
                return false;

            var doc = await _store.UpdateAsync(d =>
            {
                var vm = d.GetOrAddVm(vmId);
                vm.Credential = new StoredCredential
                {
                    Username = user,
                    ProtectedPassword = password == null ? null : _protector.Protect(password),
                };
                return d;
            }, cancellationToken).ConfigureAwait(false);

            // Persisted in the store before Notes is touched: a failure below
            // leaves the credentials in both places, never in neither.
            var payload = doc.ToNotesPayload(doc.FindVm(vmId)!);
            await _host.SetNotesAsync(vmId, NotesBlock.Compose(remaining, payload), cancellationToken).ConfigureAwait(false);
            await RecordFingerprintAsync(vmId, payload, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Moved legacy credentials of VM {VmId} from Notes into the encrypted store", vmId);
            return true;
        }

        public string? RevealPassword(StoredCredential credential)
            => credential.ProtectedPassword == null ? null : _protector.Unprotect(credential.ProtectedPassword);
    }
}
