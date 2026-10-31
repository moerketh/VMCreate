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

    /// <summary>A VM network adapter port; <see cref="SwitchId"/> is null when not connected.</summary>
    public sealed record RawSwitchPort(Guid VmId, Guid? SwitchId);

    public sealed record RawSwitch(Guid Id, string Name);

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
        /// Calls <paramref name="onChange"/> (on a background thread) when any
        /// VM is created, deleted, changes state or has its settings modified.
        /// Dispose the result to stop watching.
        /// </summary>
        IDisposable WatchChanges(Action onChange);
    }
}
