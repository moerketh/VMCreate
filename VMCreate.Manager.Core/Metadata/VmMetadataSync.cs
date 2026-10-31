using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VMCreate.Manager.HyperV;
using VMCreate.Manager.Inventory;
using VMCreate.Metadata;

namespace VMCreate.Manager.Metadata
{
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
        /// Creates store entries from Notes for VMs the store doesn't know yet
        /// (imported VMs, or a lost metadata file). Existing entries are never
        /// overwritten from Notes. Returns the number of VMs adopted.
        /// </summary>
        public Task<int> AdoptFromNotesAsync(IEnumerable<VmSummary> vms, CancellationToken cancellationToken = default)
        {
            var candidates = vms
                .Select(vm => (vm.Id, Payload: NotesBlock.Parse(vm.Notes).Payload))
                .Where(c => c.Payload != null)
                .ToList();
            if (candidates.Count == 0)
                return Task.FromResult(0);

            return _store.UpdateAsync(doc =>
            {
                int adopted = 0;
                foreach (var (id, payload) in candidates)
                {
                    if (doc.FindVm(id) != null)
                        continue;
                    doc.AdoptFromNotes(id, payload!);
                    adopted++;
                }
                return adopted;
            }, cancellationToken);
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
            _logger.LogInformation("Moved legacy credentials of VM {VmId} from Notes into the encrypted store", vmId);
            return true;
        }

        public string? RevealPassword(StoredCredential credential)
            => credential.ProtectedPassword == null ? null : _protector.Unprotect(credential.ProtectedPassword);
    }
}
