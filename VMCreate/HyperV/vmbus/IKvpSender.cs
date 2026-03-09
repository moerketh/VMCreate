using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    /// <summary>
    /// Sends Key-Value Pairs (KVP) from the Hyper-V host to a guest VM.
    /// </summary>
    public interface IKvpSender
    {
        /// <summary>
        /// Sends a KVP to the guest VM, waiting for the VM to be in a running state.
        /// </summary>
        Task SendKVPToGuestAsync(string vmName, string key, string value, CancellationToken cancellationToken = default);
    }
}
