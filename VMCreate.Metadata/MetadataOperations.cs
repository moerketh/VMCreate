namespace VMCreate.Metadata
{
    /// <summary>
    /// Pure operations on a <see cref="MetadataDocument"/>; run them inside
    /// <see cref="IVmMetadataStore.UpdateAsync{T}"/> to persist.
    /// </summary>
    public static class MetadataOperations
    {
        public const char FolderPathSeparator = '/';

        public static VmFolder CreateFolder(this MetadataDocument doc, string name, Guid? parentId)
        {
            name = ValidateFolderName(name);
            if (parentId is Guid p && doc.FindFolder(p) == null)
                throw new ArgumentException($"Parent folder {p} does not exist.", nameof(parentId));
            if (doc.Folders.Exists(f => f.ParentId == parentId && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A folder named '{name}' already exists here.");

            var folder = new VmFolder { Name = name, ParentId = parentId };
            doc.Folders.Add(folder);
            return folder;
        }

        public static void RenameFolder(this MetadataDocument doc, Guid folderId, string newName)
        {
            newName = ValidateFolderName(newName);
            var folder = doc.FindFolder(folderId) ?? throw new ArgumentException($"Folder {folderId} does not exist.", nameof(folderId));
            if (doc.Folders.Exists(f => f.Id != folderId && f.ParentId == folder.ParentId && string.Equals(f.Name, newName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A folder named '{newName}' already exists here.");
            folder.Name = newName;
        }

        public static void MoveFolder(this MetadataDocument doc, Guid folderId, Guid? newParentId)
        {
            var folder = doc.FindFolder(folderId) ?? throw new ArgumentException($"Folder {folderId} does not exist.", nameof(folderId));
            for (Guid? cursor = newParentId; cursor is Guid c; cursor = doc.FindFolder(c)?.ParentId)
            {
                if (c == folderId)
                    throw new InvalidOperationException("A folder cannot be moved into itself or one of its subfolders.");
            }
            if (newParentId is Guid p && doc.FindFolder(p) == null)
                throw new ArgumentException($"Folder {p} does not exist.", nameof(newParentId));
            if (doc.Folders.Exists(f => f.Id != folderId && f.ParentId == newParentId && string.Equals(f.Name, folder.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A folder named '{folder.Name}' already exists there.");
            folder.ParentId = newParentId;
        }

        /// <summary>
        /// Deletes a folder. Its subfolders and VMs move up to the deleted
        /// folder's parent — deleting a folder never deletes or hides VMs.
        /// </summary>
        public static void DeleteFolder(this MetadataDocument doc, Guid folderId)
        {
            var folder = doc.FindFolder(folderId) ?? throw new ArgumentException($"Folder {folderId} does not exist.", nameof(folderId));
            foreach (var child in doc.Folders.Where(f => f.ParentId == folderId).ToList())
            {
                // Avoid a duplicate name in the parent by suffixing the moved child.
                string name = child.Name;
                for (int i = 2; doc.Folders.Exists(f => f.Id != child.Id && f.ParentId == folder.ParentId && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
                    name = $"{child.Name} ({i})";
                child.Name = name;
                child.ParentId = folder.ParentId;
            }
            foreach (var vm in doc.Vms.Where(v => v.FolderId == folderId))
                vm.FolderId = folder.ParentId;
            doc.Folders.Remove(folder);
        }

        public static void MoveVmToFolder(this MetadataDocument doc, Guid vmId, Guid? folderId)
        {
            if (folderId is Guid f && doc.FindFolder(f) == null)
                throw new ArgumentException($"Folder {f} does not exist.", nameof(folderId));
            doc.GetOrAddVm(vmId).FolderId = folderId;
        }

        /// <summary>"Engagements/ACME" for a folder, or null for the root.</summary>
        public static string? GetFolderPath(this MetadataDocument doc, Guid? folderId)
        {
            var parts = new List<string>();
            var seen = new HashSet<Guid>();
            for (Guid? cursor = folderId; cursor is Guid c && seen.Add(c); )
            {
                var folder = doc.FindFolder(c);
                if (folder == null)
                    break;
                parts.Add(folder.Name);
                cursor = folder.ParentId;
            }
            if (parts.Count == 0)
                return null;
            parts.Reverse();
            return string.Join(FolderPathSeparator, parts);
        }

        /// <summary>Resolves a folder path, creating missing folders. Null/empty path = root.</summary>
        public static Guid? EnsureFolderPath(this MetadataDocument doc, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            Guid? parent = null;
            foreach (string raw in path.Split(FolderPathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var existing = doc.Folders.Find(f => f.ParentId == parent && string.Equals(f.Name, raw, StringComparison.OrdinalIgnoreCase));
                parent = (existing ?? doc.CreateFolder(raw, parent)).Id;
            }
            return parent;
        }

        /// <summary>Builds the portable Notes copy of a VM's metadata (no credentials).</summary>
        public static NotesPayload? ToNotesPayload(this MetadataDocument doc, VmMetadata vm)
        {
            var payload = new NotesPayload
            {
                Folder = doc.GetFolderPath(vm.FolderId),
                Tags = vm.Tags.Count > 0 ? vm.Tags.ToList() : null,
                Purpose = NullIfEmpty(vm.Purpose),
                Description = NullIfEmpty(vm.Description),
                Protected = vm.Protected ? true : null,
                ExpiresOn = vm.ExpiresOn,
                Provenance = vm.Provenance,
            };
            bool empty = payload.Folder == null && payload.Tags == null && payload.Purpose == null
                && payload.Description == null && payload.Protected == null && payload.ExpiresOn == null
                && payload.Provenance == null;
            return empty ? null : payload;
        }

        /// <summary>
        /// Adopts a Notes payload for a VM that has no local entry (imported
        /// VM, or a lost metadata file). Missing folders are created.
        /// </summary>
        public static VmMetadata AdoptFromNotes(this MetadataDocument doc, Guid vmId, NotesPayload payload)
        {
            var vm = doc.GetOrAddVm(vmId);
            vm.FolderId = doc.EnsureFolderPath(payload.Folder);
            vm.Tags = payload.Tags?.ToList() ?? new List<string>();
            vm.Purpose = payload.Purpose;
            vm.Description = payload.Description;
            vm.Protected = payload.Protected ?? false;
            vm.ExpiresOn = payload.ExpiresOn;
            vm.Provenance = payload.Provenance;
            return vm;
        }

        public static void SetTags(this VmMetadata vm, IEnumerable<string> tags) => vm.Tags = NormalizeTags(tags);

        /// <summary>"kali, external; acme" → [acme, external, kali]. Shared by the VMCreate GUI, CLI and Manager.</summary>
        public static List<string> ParseTags(string? text)
            => NormalizeTags((text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>Trimmed, non-empty, case-insensitively unique, sorted.</summary>
        public static List<string> NormalizeTags(IEnumerable<string> tags)
            => tags
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public static bool IsExpired(this VmMetadata vm, DateOnly today) => vm.ExpiresOn is DateOnly d && d < today;

        /// <summary>
        /// Evaluates a smart-folder filter. <paramref name="metadata"/> may be
        /// null for a VM that has never been annotated.
        /// </summary>
        public static bool Matches(this SmartFolderFilter filter, string vmName, string vmState, VmMetadata? metadata, DateOnly today)
        {
            if (filter.States.Count > 0 && !filter.States.Contains(vmState, StringComparer.OrdinalIgnoreCase))
                return false;
            if (filter.ProtectedOnly && metadata?.Protected != true)
                return false;
            if (filter.ExpiredOnly && metadata?.IsExpired(today) != true)
                return false;
            if (filter.AnyTags.Count > 0 && (metadata == null || !metadata.Tags.Any(t => filter.AnyTags.Contains(t, StringComparer.OrdinalIgnoreCase))))
                return false;
            if (!string.IsNullOrWhiteSpace(filter.Text) && !MatchesText(filter.Text, vmName, metadata))
                return false;
            return true;
        }

        public static bool MatchesText(string text, string vmName, VmMetadata? metadata)
        {
            text = text.Trim();
            if (vmName.Contains(text, StringComparison.OrdinalIgnoreCase))
                return true;
            if (metadata == null)
                return false;
            return (metadata.Purpose?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                || (metadata.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                || metadata.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        private static string ValidateFolderName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0)
                throw new ArgumentException("Folder name cannot be empty.", nameof(name));
            if (name.Contains(FolderPathSeparator))
                throw new ArgumentException($"Folder name cannot contain '{FolderPathSeparator}'.", nameof(name));
            return name;
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
