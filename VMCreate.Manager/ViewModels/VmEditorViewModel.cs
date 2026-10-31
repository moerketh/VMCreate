using VMCreate.Manager.Inventory;
using VMCreate.Metadata;

namespace VMCreate.Manager.ViewModels
{
    /// <summary>Folder choice in the details pane; Id null = no folder.</summary>
    public sealed record FolderOption(Guid? Id, string Path);

    /// <summary>
    /// Editable copy of the selected VM's name and metadata. Changes are only
    /// applied on Save, so a live refresh never clobbers half-typed text.
    /// </summary>
    public sealed class VmEditorViewModel : ObservableObject
    {
        private string _name;
        private string _purpose;
        private string _tagsText;
        private string _description;
        private bool _protected;
        private DateTime? _expiresOn;
        private Guid? _folderId;
        private string? _revealedPassword;

        public VmEditorViewModel(VmItemViewModel item)
        {
            Item = item;
            var meta = item.Metadata;
            _name = item.Name;
            _purpose = meta?.Purpose ?? "";
            _tagsText = item.TagsText;
            _description = meta?.Description ?? "";
            _protected = meta?.Protected ?? false;
            _expiresOn = meta?.ExpiresOn?.ToDateTime(TimeOnly.MinValue);
            _folderId = meta?.FolderId;
        }

        public VmItemViewModel Item { get; }
        public Guid VmId => Item.Id;
        public bool IsDirty { get; private set; }

        public string Name { get => _name; set => Edit(ref _name, value); }
        public string Purpose { get => _purpose; set => Edit(ref _purpose, value); }
        public string TagsText { get => _tagsText; set => Edit(ref _tagsText, value); }
        public string Description { get => _description; set => Edit(ref _description, value); }
        public bool Protected { get => _protected; set => Edit(ref _protected, value); }
        public DateTime? ExpiresOn { get => _expiresOn; set => Edit(ref _expiresOn, value); }
        public Guid? FolderId { get => _folderId; set => Edit(ref _folderId, value); }

        public bool WasProtected => Item.Protected;

        // ── read-only facts ──
        public VmSummary Summary => Item.Summary;
        public string? Username => Item.Metadata?.Credential?.Username;
        public bool HasCredential => Item.Metadata?.Credential != null;
        public bool HasPassword => Item.Metadata?.Credential?.ProtectedPassword != null;
        public VmProvenance? Provenance => Item.Metadata?.Provenance;
        public string ProvenanceText => Provenance == null ? "" : string.Join("  ·  ", new[]
            {
                Provenance.Distribution ?? Provenance.GalleryItem,
                Provenance.Version,
                Provenance.CreatedAt?.LocalDateTime.ToString("yyyy-MM-dd"),
                Provenance.Tool,
            }.Where(s => !string.IsNullOrEmpty(s)));

        public string? RevealedPassword
        {
            get => _revealedPassword;
            set => SetProperty(ref _revealedPassword, value);
        }

        public IEnumerable<string> ParsedTags => TagsText.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>Copies the edited fields onto the stored entry.</summary>
        public void ApplyTo(VmMetadata vm)
        {
            vm.Purpose = string.IsNullOrWhiteSpace(Purpose) ? null : Purpose.Trim();
            vm.Description = string.IsNullOrWhiteSpace(Description) ? null : Description;
            vm.SetTags(ParsedTags);
            vm.Protected = Protected;
            vm.ExpiresOn = ExpiresOn is DateTime d ? DateOnly.FromDateTime(d) : null;
            vm.FolderId = FolderId;
        }

        public void MarkClean()
        {
            IsDirty = false;
            OnPropertyChanged(nameof(IsDirty));
        }

        private void Edit<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (SetProperty(ref field, value, name) && !IsDirty)
            {
                IsDirty = true;
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }
}
