using VMCreate.Manager.Inventory;
using VMCreate.Metadata;

namespace VMCreate.Manager.ViewModels
{
    /// <summary>One grid row: live Hyper-V state joined with stored metadata.</summary>
    public sealed class VmItemViewModel : ObservableObject
    {
        public VmItemViewModel(VmSummary summary, VmMetadata? metadata, string? folderPath, string? folderColor)
        {
            Summary = summary;
            Metadata = metadata;
            FolderPath = folderPath;
            FolderColor = folderColor;
        }

        public VmSummary Summary { get; private set; }
        public VmMetadata? Metadata { get; private set; }
        public string? FolderPath { get; private set; }
        /// <summary>Grid group in the "All" view: folder path, empty for unfiled.</summary>
        public string GroupKey => FolderPath ?? "";
        /// <summary>Folder icon colour (own or inherited), shown on the group header.</summary>
        public string? FolderColor { get; private set; }

        public Guid Id => Summary.Id;
        public string Name => Summary.Name;
        public VmState State => Summary.State;
        public string StateText => DisplayFormat.State(Summary.State);
        public string CpuText => Summary.CpuPercent is int c ? $"{c}%" : "";
        public string MemoryText => Summary.AssignedMemoryMb is ulong m ? DisplayFormat.Memory(m) : DisplayFormat.Memory(Summary.StartupMemoryMb);
        public bool MemoryIsLive => Summary.AssignedMemoryMb != null;
        public string UptimeText => DisplayFormat.Uptime(Summary.Uptime);
        public string? PrimaryIp => Summary.IpAddresses.FirstOrDefault();
        public string IpText => string.Join(", ", Summary.IpAddresses);
        public string SwitchText => string.Join(", ", Summary.Switches.Distinct());
        public int CheckpointCount => Summary.CheckpointCount;

        public Guid? FolderId => Metadata?.FolderId;
        public string TagsText => Metadata == null ? "" : string.Join(", ", Metadata.Tags);
        public string? Purpose => Metadata?.Purpose;
        public bool Protected => Metadata?.Protected ?? false;
        public DateOnly? ExpiresOn => Metadata?.ExpiresOn;
        public bool IsExpired => Metadata?.IsExpired(DateOnly.FromDateTime(DateTime.Today)) ?? false;

        public void Update(VmSummary summary, VmMetadata? metadata, string? folderPath, string? folderColor)
        {
            Summary = summary;
            Metadata = metadata;
            FolderPath = folderPath;
            FolderColor = folderColor;
            OnPropertyChanged(string.Empty); // every derived property may have changed
        }
    }
}
