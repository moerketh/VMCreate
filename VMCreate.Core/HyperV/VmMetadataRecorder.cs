using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.Gallery;
using VMCreate.HyperV;
using VMCreate.Metadata;

namespace VMCreate
{
    /// <summary>
    /// Writes what VMCreate knows about a VM it just deployed into the metadata
    /// store shared with VMCreate Manager: login credentials (encrypted),
    /// provenance, folder and tags. The portable copy in the VM's Notes is
    /// refreshed too — without credentials.
    /// </summary>
    public interface IVmMetadataRecorder
    {
        /// <summary>
        /// Stores the gallery item's published login (e.g. kali/kali) for the
        /// VM, password DPAPI-encrypted. No-op when both values are empty.
        /// </summary>
        Task RecordCredentialsAsync(string vmName, string? username, string? password, CancellationToken cancellationToken);

        /// <summary>Records provenance, folder and tags of a finished deployment and mirrors them into Notes.</summary>
        Task RecordDeploymentAsync(VmDeploymentPlan plan, GalleryItem galleryItem, CancellationToken cancellationToken);
    }

    public sealed class VmMetadataRecorder : IVmMetadataRecorder
    {
        private readonly IPowerShellExecutor _executor;
        private readonly IVmMetadataStore _store;
        private readonly ICredentialProtector _protector;
        private readonly ILogger<VmMetadataRecorder> _logger;

        public VmMetadataRecorder(IPowerShellExecutor executor, IVmMetadataStore store, ICredentialProtector protector, ILogger<VmMetadataRecorder> logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task RecordCredentialsAsync(string vmName, string? username, string? password, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
                return;

            Guid vmId = (await GetVmAsync(vmName, cancellationToken)).Id;
            await _store.UpdateAsync(doc =>
            {
                doc.GetOrAddVm(vmId).Credential = new StoredCredential
                {
                    Username = string.IsNullOrEmpty(username) ? null : username,
                    ProtectedPassword = string.IsNullOrEmpty(password) ? null : _protector.Protect(password),
                };
                return true;
            }, cancellationToken);
            _logger.LogInformation("Stored login credentials of {VMName} in the encrypted metadata store", vmName);
        }

        public async Task RecordDeploymentAsync(VmDeploymentPlan plan, GalleryItem galleryItem, CancellationToken cancellationToken)
        {
            var (vmId, notes) = await GetVmAsync(plan.VmName, cancellationToken);

            var doc = await _store.UpdateAsync(d =>
            {
                ApplyDeployment(d, d.GetOrAddVm(vmId), plan, galleryItem, DateTimeOffset.Now);
                return d;
            }, cancellationToken);

            string updated = NotesBlock.Update(notes, doc.ToNotesPayload(doc.FindVm(vmId)!));
            if (updated != notes)
            {
                var result = await _executor.RunCommandAsync("Set-VM",
                    new Dictionary<string, object?> { ["Name"] = plan.VmName, ["Notes"] = updated }, cancellationToken);
                if (result.HadErrors)
                    throw new Exception($"Failed to update VM notes: {result.ErrorSummary}");
            }
            _logger.LogInformation("Recorded deployment metadata for {VMName} (folder {Folder}, tags {Tags})",
                plan.VmName, plan.Folder ?? "(none)", string.Join(",", plan.Tags));
        }

        /// <summary>Pure part of <see cref="RecordDeploymentAsync"/>: what lands in the store.</summary>
        internal static void ApplyDeployment(MetadataDocument doc, VmMetadata vm, VmDeploymentPlan plan, GalleryItem item, DateTimeOffset now)
        {
            vm.Provenance = new VmProvenance
            {
                Tool = "VMCreate",
                ToolVersion = ProductInfo.Version,
                GalleryItem = item.Name,
                Distribution = item.LinuxDistro == LinuxDistro.Unknown ? item.Name : item.LinuxDistro.ToString(),
                Version = string.IsNullOrWhiteSpace(item.Version) ? null : item.Version,
                ImageChecksum = string.IsNullOrWhiteSpace(item.Checksum) ? null : item.Checksum,
                CreatedAt = now,
            };
            if (!string.IsNullOrWhiteSpace(plan.Folder))
                vm.FolderId = doc.EnsureFolderPath(plan.Folder);
            if (plan.Tags.Count > 0)
                vm.SetTags(vm.Tags.Concat(plan.Tags));
        }

        private async Task<(Guid Id, string Notes)> GetVmAsync(string vmName, CancellationToken cancellationToken)
        {
            var result = await _executor.RunCommandAsync("Get-VM",
                new Dictionary<string, object?> { ["Name"] = vmName }, cancellationToken);
            var vm = result.Output.FirstOrDefault();
            if (result.HadErrors || vm == null || vm.Properties["Id"]?.Value is not Guid id)
                throw new Exception($"Could not read VM '{vmName}': {result.ErrorSummary}");
            return (id, vm.Properties["Notes"]?.Value as string ?? "");
        }
    }
}
