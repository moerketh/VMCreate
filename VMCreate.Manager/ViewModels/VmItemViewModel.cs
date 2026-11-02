using VMCreate.Manager.Inventory;
using VMCreate.Metadata;

namespace VMCreate.Manager.ViewModels
{
    /// <summary>What a VM takes from its folder: path, icon colour and (inherited) expiry.</summary>
    public sealed record VmFolderContext(string? Path, string? Color, DateOnly? EffectiveExpiry, string? ExpiryFolder)
    {
        public static VmFolderContext From(MetadataDocument doc, VmMetadata? meta)
        {
            var (folderExpiry, from) = doc.GetFolderExpiry(meta?.FolderId);
            bool inherited = meta?.ExpiresOn == null && folderExpiry != null;
            return new VmFolderContext(
                doc.GetFolderPath(meta?.FolderId),
                doc.GetFolderColor(meta?.FolderId),
                meta?.ExpiresOn ?? folderExpiry,
                inherited ? from!.Name : null);
        }
    }

    /// <summary>One grid row: live Hyper-V state joined with stored metadata.</summary>
    public sealed class VmItemViewModel : ObservableObject
    {
        public VmItemViewModel(VmSummary summary, VmMetadata? metadata, VmFolderContext folder)
        {
            Summary = summary;
            Metadata = metadata;
            Folder = folder;
        }

        public VmSummary Summary { get; private set; }
        public VmMetadata? Metadata { get; private set; }
        public VmFolderContext Folder { get; private set; }
        public string? FolderPath => Folder.Path;
        /// <summary>Grid group in the "All" view: folder path, empty for unfiled.</summary>
        public string GroupKey => FolderPath ?? "";
        /// <summary>Folder icon colour (own or inherited), shown on the group header.</summary>
        public string? FolderColor => Folder.Color;

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
        public NetworkExposure Exposure => Summary.Exposure;
        public bool CanReconnectNetwork => Summary.DisconnectedAdapterCount > 0;

        /// <summary>Badge tooltip, e.g. "External: bridged onto the physical network (LAN)".</summary>
        public string ExposureText => Summary.Exposure switch
        {
            NetworkExposure.External => $"External: bridged onto the physical network ({SwitchText})",
            NetworkExposure.Nat => $"NAT: outbound internet through the host ({SwitchText})",
            NetworkExposure.Internal => $"Internal: reaches the host ({SwitchText})",
            NetworkExposure.Private => $"Private: other VMs on the switch only ({SwitchText})",
            _ => CanReconnectNetwork ? "Isolated: network adapters disconnected" : "No network",
        };

        public Guid? FolderId => Metadata?.FolderId;
        public string TagsText => Metadata == null ? "" : string.Join(", ", Metadata.Tags);
        public string? Purpose => Metadata?.Purpose;
        public bool Protected => Metadata?.Protected ?? false;
        public bool IsTemplate => Metadata?.IsTemplate ?? false;
        public Guid? TemplateId => Metadata?.TemplateId;
        /// <summary>The VM's own expiry date (what the editor edits).</summary>
        public DateOnly? ExpiresOn => Metadata?.ExpiresOn;
        /// <summary>Own date, else the folder's: what "expired" is judged on.</summary>
        public DateOnly? EffectiveExpiresOn => Folder.EffectiveExpiry;
        public bool IsExpired => MetadataOperations.IsPast(EffectiveExpiresOn, DateOnly.FromDateTime(DateTime.Today));
        public string ExpiryText => EffectiveExpiresOn is not DateOnly d ? ""
            : (IsExpired ? "Expired " : "Expires ") + d.ToString("yyyy-MM-dd")
              + (Folder.ExpiryFolder is string f ? $" (folder '{f}')" : "");

        public void Update(VmSummary summary, VmMetadata? metadata, VmFolderContext folder)
        {
            Summary = summary;
            Metadata = metadata;
            Folder = folder;
            OnPropertyChanged(string.Empty); // every derived property may have changed
        }
    }
}
