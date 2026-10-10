using System;
using System.Security.Cryptography;
using System.Text;

namespace VMCreate
{
    /// <summary>
    /// Masks secrets for logs and UI: keeps at most the last 4 characters,
    /// never reveals the full value. Short secrets collapse to bullets.
    /// </summary>
    public static class SecretMask
    {
        /// <summary>Returns a log-safe representation of <paramref name="secret"/>. Null/empty stays null/empty.</summary>
        public static string? Mask(string? secret)
        {
            if (string.IsNullOrEmpty(secret))
                return secret;

            const int tail = 4;
            if (secret.Length <= tail)
                return new string('•', secret.Length);

            return new string('•', 8) + secret[^tail..];
        }
    }

    /// <summary>
    /// Reversible protection for secrets at rest. The default implementation
    /// uses Windows DPAPI (user scope) via
    /// <see cref="System.Security.Cryptography.ProtectedData"/>, so secrets are
    /// only decryptable by the same Windows user on the same machine — no key
    /// material is ever written to disk.
    /// </summary>
    public interface ISecretProtector
    {
        /// <summary>Protects <paramref name="plaintext"/> for storage. Returns null for null input.</summary>
        string? Protect(string? plaintext);

        /// <summary>Unprotects a value produced by <see cref="Protect"/>. Returns null for null input.</summary>
        string? Unprotect(string? protectedValue);
    }

    /// <summary>
    /// DPAPI (user scope) implementation of <see cref="ISecretProtector"/>.
    /// The additional entropy is an app-specific constant that raises the bar
    /// for offline tampering with the settings file; DPAPI's master key —
    /// derived from the Windows user credentials and never on disk — remains
    /// the actual secret.
    /// </summary>
    public sealed class DpapiSecretProtector : ISecretProtector
    {
        // Marker prefix so the store can distinguish protected values from
        // legacy plaintext and migrate them on first save.
        public const string ProtectedPrefix = "dpapi:";

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VMCreate.UserPreferences.v1");

        public string? Protect(string? plaintext)
        {
            if (plaintext == null)
                return null;

            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return ProtectedPrefix + Convert.ToBase64String(protectedBytes);
        }

        public string? Unprotect(string? protectedValue)
        {
            if (protectedValue == null)
                return null;

            if (!protectedValue.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
                return protectedValue; // legacy plaintext — pass through (re-protected on next save)

            try
            {
                var bytes = Convert.FromBase64String(protectedValue[ProtectedPrefix.Length..]);
                var decrypted = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch (CryptographicException)
            {
                return null; // e.g. file copied from another machine/user — treat as unset
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
