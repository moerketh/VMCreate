namespace VMCreate.Manager.HyperV
{
    /// <summary>
    /// Raw per-VM facts read from root\virtualization\v2. Kept close to the
    /// WMI shape so <see cref="WmiHyperVHost"/> stays a thin mapper and the
    /// joining logic in <see cref="Inventory.VmInventoryService"/> is testable.
    /// </summary>
    public sealed record RawVm(
        Guid Id,
        string Name,
        ushort EnabledState,
        ulong OnTimeMilliseconds,
        int Generation,
        string? ConfigurationPath,
        string Notes,
        DateTime? CreatedAt);

    public sealed record RawVmSummary(
        Guid Id,
        int ProcessorCount,
        int? ProcessorLoadPercent,
        ulong? MemoryUsageMb,
        ushort? Heartbeat,
        string? GuestOperatingSystem);

    public sealed record RawGuestNetworkAdapter(Guid VmId, IReadOnlyList<string> IpAddresses);

    /// <summary>
    /// A VM network adapter's switch port. <see cref="SwitchId"/> is the configured
    /// switch, which Hyper-V keeps while the adapter is disconnected
    /// (<see cref="Connected"/> false); null when it was never connected.
    /// </summary>
    public sealed record RawSwitchPort(Guid VmId, Guid? SwitchId, bool Connected = true);

    /// <summary>How far a virtual switch reaches beyond the VMs on it.</summary>
    public enum SwitchKind
    {
        /// <summary>VM-to-VM only.</summary>
        Private,
        /// <summary>VMs and the host.</summary>
        Internal,
        /// <summary>Bridged onto a physical network adapter.</summary>
        External,
    }

    /// <param name="Nat">Internal switch whose host side NATs to the outside world (Default Switch, NetNat).</param>
    public sealed record RawSwitch(Guid Id, string Name, SwitchKind Kind = SwitchKind.Private, bool Nat = false);

    public sealed record RawMemorySetting(Guid VmId, ulong StartupMb, bool DynamicMemoryEnabled);

    public sealed record RawCheckpoint(Guid VmId, string Name);

    /// <summary>Everything the inventory needs, read in one pass.</summary>
    public sealed record HostSnapshot(
        IReadOnlyList<RawVm> Vms,
        IReadOnlyList<RawVmSummary> Summaries,
        IReadOnlyList<RawGuestNetworkAdapter> GuestAdapters,
        IReadOnlyList<RawSwitchPort> SwitchPorts,
        IReadOnlyList<RawSwitch> Switches,
        IReadOnlyList<RawMemorySetting> Memory,
        IReadOnlyList<RawCheckpoint> Checkpoints);

    public enum AttachedMediaKind
    {
        VirtualHardDisk,
        Iso,
        Floppy,
    }

    /// <summary>
    /// A file attached to a VM's drive, in its current configuration
    /// (<see cref="CheckpointName"/> null) or in one of its checkpoints.
    /// <see cref="VmId"/> is null when the owner is not a realized VM (a planned
    /// or half-imported system): treat such files as in use by someone else.
    /// </summary>
    public sealed record RawAttachment(Guid? VmId, string? CheckpointName, string Path, AttachedMediaKind Kind);

    /// <summary>
    /// An attached file (virtual disk, ISO, floppy) or a virtual disk's ancestor.
    /// For virtual disks this is what the image service reports, and
    /// <see cref="Exists"/> false means missing or unreadable: the parent chain is then unknown.
    /// </summary>
    /// <param name="Type">Virtual disks: 2 fixed, 3 dynamic, 4 differencing (Msvm_VirtualHardDiskSettingData.Type); 0 otherwise.</param>
    public sealed record RawStorageFile(string Path, bool Exists, ushort Type, string? ParentPath, long? SizeBytes);

    public sealed record RawVmStorageRoots(Guid VmId, string? ConfigurationRoot, string? SnapshotRoot, string? SwapFileRoot);

    /// <summary>A checkpoint with its creation time, for the storage overview.</summary>
    public sealed record RawCheckpointInfo(Guid VmId, Guid Id, string Name, DateTime? CreatedAt);

    /// <summary>
    /// Every disk and ISO reference on the host, the virtual disks' parent links,
    /// and the folders VMs live in. Input of <see cref="Deletion.DeletionPlanner"/>.
    /// </summary>
    /// <param name="Disks">Every attached file plus all virtual disk ancestors, keyed by full path (case-insensitive).</param>
    /// <param name="HostDefaultRoots">Hyper-V's default VM and virtual hard disk folders: shared, never removed.</param>
    /// <param name="KnownSystemIds">
    /// Every VM, checkpoint and planned (being imported) system ID Hyper-V knows:
    /// configuration files (*.vmcx) named after any of these are in use.
    /// </param>
    public sealed record StorageGraph(
        IReadOnlyList<RawAttachment> Attachments,
        IReadOnlyDictionary<string, RawStorageFile> Files,
        IReadOnlyList<RawVmStorageRoots> Roots,
        IReadOnlyList<string> HostDefaultRoots,
        IReadOnlySet<Guid>? KnownSystemIds = null,
        IReadOnlyList<RawCheckpointInfo>? Checkpoints = null)
    {
        public IReadOnlySet<Guid> SystemIds => KnownSystemIds ?? new HashSet<Guid>(Roots.Select(r => r.VmId));
        public IReadOnlyList<RawCheckpointInfo> CheckpointList => Checkpoints ?? Array.Empty<RawCheckpointInfo>();
    }

    /// <summary>
    /// A console screenshot: <see cref="Pixels"/> is RGB565, 2 bytes per
    /// pixel, rows top to bottom, stride = Width * 2.
    /// </summary>
    public sealed record VmThumbnail(int Width, int Height, byte[] Pixels);

    /// <summary>States accepted by Msvm_ComputerSystem.RequestStateChange.</summary>
    public enum RequestedVmState : ushort
    {
        Running = 2,
        Off = 3,
        Saved = 6,
        Paused = 9,
        Reset = 11,
    }

    public sealed class HyperVOperationException : Exception
    {
        public uint? ReturnCode { get; }

        public HyperVOperationException(string message, uint? returnCode = null, Exception? inner = null)
            : base(message, inner) => ReturnCode = returnCode;
    }

    /// <summary>Local Hyper-V host access. One implementation today: WMI.</summary>
    public interface IHyperVHost
    {
        Task<HostSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);

        Task RequestStateChangeAsync(Guid vmId, RequestedVmState state, CancellationToken cancellationToken = default);

        /// <summary>Guest OS shutdown through the Shutdown integration service.</summary>
        Task ShutdownAsync(Guid vmId, bool force, CancellationToken cancellationToken = default);

        Task<string> GetNotesAsync(Guid vmId, CancellationToken cancellationToken = default);

        Task SetNotesAsync(Guid vmId, string notes, CancellationToken cancellationToken = default);

        Task RenameAsync(Guid vmId, string newName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Console screenshot scaled to fit <paramref name="maxWidth"/> x
        /// <paramref name="maxHeight"/> keeping the guest's aspect ratio.
        /// Null when the VM has no display to capture (off, or no video yet).
        /// </summary>
        Task<VmThumbnail?> GetThumbnailAsync(Guid vmId, int maxWidth, int maxHeight, CancellationToken cancellationToken = default);

        /// <summary>
        /// Disconnects every connected network adapter of the VM (works while it
        /// runs). Hyper-V keeps the switch configured, so the change is reversible.
        /// Returns the IDs of the ports that were disconnected.
        /// </summary>
        Task<IReadOnlyList<string>> DisconnectNetworkAsync(Guid vmId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reconnects the given ports (from <see cref="DisconnectNetworkAsync"/>) to
        /// their configured switch; null reconnects every adapter that has a switch.
        /// Returns how many ports were reconnected.
        /// </summary>
        Task<int> ReconnectNetworkAsync(Guid vmId, IReadOnlyCollection<string>? portIds, CancellationToken cancellationToken = default);

        /// <summary>Connects every network adapter of the VM to one switch. Returns the adapter count.</summary>
        Task<int> ConnectNetworkToSwitchAsync(Guid vmId, Guid switchId, CancellationToken cancellationToken = default);

        /// <summary>The host's default folder for new VMs (Hyper-V Settings → Virtual Machines).</summary>
        Task<string> GetDefaultVmFolderAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a VM with the template's settings whose virtual hard disks are
        /// differencing disks on top of the template's. Everything lands in
        /// <paramref name="folder"/> (which must not exist yet). The template must be
        /// off. On failure everything created so far is removed again.
        /// </summary>
        /// <returns>The new VM's ID.</returns>
        Task<Guid> CreateLinkedCloneAsync(Guid templateId, string cloneName, string folder, string notes, CancellationToken cancellationToken = default);

        /// <summary>Reads every VM's disk and ISO references and the virtual disks' parent chains.</summary>
        Task<StorageGraph> ReadStorageGraphAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Asks the image service about virtual disk files no VM references
        /// (type, parent). Unreadable files come back with Exists false.
        /// </summary>
        Task<IReadOnlyList<RawStorageFile>> InspectVirtualDisksAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes the VM from Hyper-V: its configuration, checkpoints and saved
        /// state. Hyper-V leaves virtual hard disks and ISOs on disk.
        /// </summary>
        Task DestroyVmAsync(Guid vmId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Full export (configuration, checkpoints, saved state, disks) into
        /// <paramref name="directory"/>, which must be empty or not exist yet. The
        /// result can be imported with Hyper-V Manager's "Import Virtual Machine".
        /// </summary>
        Task ExportVmAsync(Guid vmId, string directory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Calls <paramref name="onChange"/> (on a background thread) when any
        /// VM is created, deleted, changes state or has its settings modified.
        /// Dispose the result to stop watching.
        /// </summary>
        IDisposable WatchChanges(Action onChange);
    }
}
