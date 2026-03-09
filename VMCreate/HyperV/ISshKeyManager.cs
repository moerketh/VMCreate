using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Manages SSH keypairs for secure key-based authentication with guest VMs.
    /// </summary>
    public interface ISshKeyManager
    {
        /// <summary>Absolute path to the private key file.</summary>
        string PrivateKeyPath { get; }

        /// <summary>Absolute path to the public key file.</summary>
        string PublicKeyPath { get; }

        /// <summary>
        /// Ensures the SSH keypair exists. Generates one if missing.
        /// Returns the public key content for injection into VMs.
        /// </summary>
        Task<string> EnsureKeyPairAsync(CancellationToken ct = default);

        /// <summary>
        /// Reads the public key content from a custom key file path.
        /// </summary>
        Task<string> ReadPublicKeyAsync(string customPublicKeyPath, CancellationToken ct = default);

        /// <summary>
        /// Gets the private key path to use for connections.
        /// Returns the custom key path if provided and valid, otherwise the auto-generated key.
        /// </summary>
        string GetPrivateKeyPath(string customPublicKeyPath = null);
    }
}
