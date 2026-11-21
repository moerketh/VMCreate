using System.Security.Cryptography;
using System.Text;

namespace VMCreate.Metadata
{
    /// <summary>Encrypts VM passwords at rest in the metadata file.</summary>
    public interface ICredentialProtector
    {
        string Protect(string plaintext);
        string Unprotect(string protectedValue);
    }

    /// <summary>
    /// DPAPI, CurrentUser scope: only the Windows account that stored the
    /// password can read it back (elevated or not — elevation keeps the same
    /// user profile). The metadata file itself lives in that user's profile too.
    /// </summary>
    public sealed class DpapiCredentialProtector : ICredentialProtector
    {
        // Application-specific entropy so another DPAPI consumer running as the
        // same user cannot decrypt these blobs by accident.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VMCreate.Metadata.Credential.v1");

        public string Protect(string plaintext)
        {
            byte[] data = Encoding.UTF8.GetBytes(plaintext);
            return Convert.ToBase64String(ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
        }

        public string Unprotect(string protectedValue)
        {
            byte[] blob = Convert.FromBase64String(protectedValue);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
        }
    }
}
