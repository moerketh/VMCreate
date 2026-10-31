using System.Text.Json.Serialization;

namespace VMCreate.Metadata
{
    /// <summary>
    /// Root of the metadata file. Everything is keyed on the Hyper-V VM ID
    /// (GUID), never the VM name: renames and duplicate names must not lose
    /// or cross-wire metadata.
    /// </summary>
    public sealed class MetadataDocument
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<VmFolder> Folders { get; set; } = new();
        public List<SmartFolder> SmartFolders { get; set; } = new();
        public List<VmMetadata> Vms { get; set; } = new();

        public VmMetadata? FindVm(Guid vmId) => Vms.Find(v => v.VmId == vmId);

        public VmFolder? FindFolder(Guid folderId) => Folders.Find(f => f.Id == folderId);

        /// <summary>Returns the entry for <paramref name="vmId"/>, creating an empty one when absent.</summary>
        public VmMetadata GetOrAddVm(Guid vmId)
        {
            var vm = FindVm(vmId);
            if (vm == null)
            {
                vm = new VmMetadata { VmId = vmId };
                Vms.Add(vm);
            }
            return vm;
        }
    }

    public sealed class VmMetadata
    {
        public Guid VmId { get; set; }

        /// <summary>Containing folder; null means the root.</summary>
        public Guid? FolderId { get; set; }

        public List<string> Tags { get; set; } = new();

        /// <summary>Free-form Markdown description.</summary>
        public string? Description { get; set; }

        /// <summary>One-line purpose, shown in the grid ("ACME external pentest jump box").</summary>
        public string? Purpose { get; set; }

        /// <summary>
        /// Delete protection. Enforced by VMCreate tools only — it cannot stop
        /// Remove-VM or the Hyper-V Manager MMC.
        /// </summary>
        public bool Protected { get; set; }

        /// <summary>Date after which the VM is shown as expired. Never triggers automatic deletion.</summary>
        public DateOnly? ExpiresOn { get; set; }

        public VmProvenance? Provenance { get; set; }

        /// <summary>Credentials; the password is DPAPI-protected and never mirrored into Notes.</summary>
        public StoredCredential? Credential { get; set; }
    }

    /// <summary>How a VM was built, recorded by VMCreate at deploy time.</summary>
    public sealed class VmProvenance
    {
        /// <summary>Creating tool, e.g. "VMCreate" or "VMCreate.CLI".</summary>
        public string? Tool { get; set; }
        public string? ToolVersion { get; set; }
        public string? GalleryItem { get; set; }
        public string? Distribution { get; set; }
        public string? Version { get; set; }
        public string? ImageChecksum { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public List<string> Customizations { get; set; } = new();
    }

    public sealed class StoredCredential
    {
        public string? Username { get; set; }

        /// <summary>Base64 DPAPI blob produced by <see cref="ICredentialProtector"/>.</summary>
        public string? ProtectedPassword { get; set; }
    }

    public sealed class VmFolder
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";

        /// <summary>Parent folder; null means a top-level folder.</summary>
        public Guid? ParentId { get; set; }
    }

    /// <summary>A saved filter shown alongside the folder tree.</summary>
    public sealed class SmartFolder
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public SmartFolderFilter Filter { get; set; } = new();
    }

    public sealed class SmartFolderFilter
    {
        /// <summary>Matches VMs carrying any of these tags (case-insensitive). Empty = no tag condition.</summary>
        public List<string> AnyTags { get; set; } = new();

        /// <summary>Matches VMs in any of these states (Hyper-V state names, e.g. "Running"). Empty = any state.</summary>
        public List<string> States { get; set; } = new();

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ExpiredOnly { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ProtectedOnly { get; set; }

        /// <summary>Substring matched against name, purpose, description and tags.</summary>
        public string? Text { get; set; }
    }
}
