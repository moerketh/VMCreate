namespace VMCreate.Manager.Inventory
{
    public enum VmState
    {
        Unknown,
        Running,
        Off,
        Saved,
        Paused,
        Starting,
        Stopping,
        Saving,
        Pausing,
        Resuming,
    }

    public static class VmStateMapper
    {
        /// <summary>
        /// Maps Msvm_ComputerSystem.EnabledState. root\virtualization\v2 uses
        /// the CIM values (6 = saved, 9 = paused, 10 = starting); the legacy
        /// 327xx values are mapped too since some builds still report them.
        /// </summary>
        public static VmState FromEnabledState(ushort enabledState) => enabledState switch
        {
            2 => VmState.Running,
            3 => VmState.Off,
            6 or 32769 or 32779 => VmState.Saved,
            9 or 32768 => VmState.Paused,
            10 or 32770 => VmState.Starting,
            4 or 32774 => VmState.Stopping,
            32773 or 32780 => VmState.Saving,
            32776 => VmState.Pausing,
            32777 => VmState.Resuming,
            _ => VmState.Unknown,
        };
    }

    /// <summary>One VM as shown in the Manager: the joined, display-ready view of the host snapshot.</summary>
    /// <summary>How far a VM's network reaches, least to most exposed.</summary>
    public enum NetworkExposure
    {
        /// <summary>No adapter, or none connected.</summary>
        None,
        /// <summary>Private switches only: other VMs on the same switch.</summary>
        Private,
        /// <summary>Internal switch: the host too.</summary>
        Internal,
        /// <summary>Internal switch that NATs out (Default Switch): outbound internet through the host.</summary>
        Nat,
        /// <summary>External switch: bridged onto the physical network.</summary>
        External,
    }

    public sealed record VmSummary
    {
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public required VmState State { get; init; }
        public TimeSpan Uptime { get; init; }
        public int Generation { get; init; }
        public string? ConfigurationPath { get; init; }

        /// <summary>Raw Hyper-V Notes, including any [vmcreate] block.</summary>
        public string Notes { get; init; } = "";

        public DateTime? CreatedAt { get; init; }
        public int ProcessorCount { get; init; }
        public int? CpuPercent { get; init; }

        /// <summary>Memory currently assigned; null when the VM is not running.</summary>
        public ulong? AssignedMemoryMb { get; init; }

        public ulong StartupMemoryMb { get; init; }
        public bool DynamicMemory { get; init; }
        public string? GuestOperatingSystem { get; init; }
        public IReadOnlyList<string> IpAddresses { get; init; } = Array.Empty<string>();

        /// <summary>Switch name per network adapter; "Not connected" for unplugged adapters.</summary>
        public IReadOnlyList<string> Switches { get; init; } = Array.Empty<string>();

        /// <summary>Widest reach of any connected adapter.</summary>
        public NetworkExposure Exposure { get; init; }

        /// <summary>Adapters disconnected from a switch they remember (what "Isolate now" leaves behind).</summary>
        public int DisconnectedAdapterCount { get; init; }

        public int CheckpointCount { get; init; }

        public bool IsRunning => State is VmState.Running or VmState.Paused or VmState.Starting or VmState.Stopping
            or VmState.Saving or VmState.Pausing or VmState.Resuming;
    }
}
