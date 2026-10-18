using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    /// <summary>
    /// KVP maintenance operations the v2 ISO cycle needs beyond plain Add:
    /// removal (finally-path hygiene), in-place modification (Add-then-
    /// Modify replace semantics) and GUID resolution (the vsock host string).
    /// </summary>
    public interface IKvpMaintenance
    {
        /// <summary>Removes a host-sent (Source=0) KVP item. DataItems MUST
        /// be embedded-XML strings (live-verified form); works with the VM
        /// powered off.</summary>
        Task RemoveKvpItemAsync(string vmName, string name, string? data, CancellationToken ct = default);

        /// <summary>Modifies an existing host-sent KVP item in place. Used
        /// when Add fails (JobState 10 is a generic failure, not
        /// specifically "duplicate"); if Modify also fails, callers surface
        /// the ORIGINAL Add error.</summary>
        Task ModifyKvpItemAsync(string vmName, string name, string data, CancellationToken ct = default);

        /// <summary>Resolves the VM's Hyper-V GUID regardless of power state
        /// (the vsock SSH host string). Throws on ambiguous names.</summary>
        Task<string?> ResolveVmGuidAsync(string vmName, CancellationToken ct = default);
    }
}
