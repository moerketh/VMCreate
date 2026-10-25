using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace VMCreate
{
    /// <summary>
    /// The trust anchor for vmcreate-iso releases: the public keys whose
    /// signatures on a release manifest make an ISO bootable. VMCreate only
    /// boots an ISO whose manifest is signed by one of these keys —
    /// <c>vmcreate-iso</c>'s signing keys are pinned here the same way they
    /// are pinned in that repo's <c>keys/</c> directory.
    /// <para>
    /// key_id convention (shared with vmcreate-iso): the first 16 hex
    /// characters of SHA-256 over the key's DER
    /// SubjectPublicKeyInfo — <see cref="ExportSubjectPublicKeyInfo"/> in
    /// .NET, <c>openssl pkey -pubout -outform DER | sha256sum</c> on the
    /// release side.
    /// </para>
    /// </summary>
    public sealed class IsoReleaseTrust
    {
        /// <summary>
        /// Keys shipped with VMCreate: the vmcreate-iso release signing
        /// keys. A release signed by any other key is not trusted.
        /// </summary>
        private const string PrimarySigningKeyPem = """
            -----BEGIN PUBLIC KEY-----
            MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEQ/76QinU7KWchR59pH0ZMEzySPOn
            SK13zpPy2xOZA7IBcqc7ZM8KNQR0Kv9L6quwF1GGm+kkFF+kiMrCcgAg2w==
            -----END PUBLIC KEY-----
            """;

        private const string SecondarySigningKeyPem = """
            -----BEGIN PUBLIC KEY-----
            MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEx58kzR3d6TTdlkHGY7jmVKfLGx08
            Ig2bS4bYfL8S/WlvUsBTSKsksPOPIYfD3Bhru0ni1YNXvyJJoJGERjKDqQ==
            -----END PUBLIC KEY-----
            """;

        private readonly IReadOnlyList<ECDsa> _keys;
        private readonly IReadOnlyDictionary<string, ECDsa> _keyMap;

        /// <summary>
        /// The production trust store: the vmcreate-iso signing keys built
        /// into VMCreate, keyed by their key_id.
        /// </summary>
        public static IsoReleaseTrust BuiltIn { get; } = new();

        /// <summary>
        /// Test seam: trust exactly the given keys instead of the built-in
        /// set, so no test key ever ships. Internal — production code always
        /// uses <see cref="BuiltIn"/>.
        /// </summary>
        internal IsoReleaseTrust(params string[] pemKeys)
        {
            var keys = new Dictionary<string, ECDsa>(StringComparer.OrdinalIgnoreCase);
            foreach (string pem in pemKeys.Length > 0 ? pemKeys : new[] { PrimarySigningKeyPem, SecondarySigningKeyPem })
            {
                ECDsa key = ECDsa.Create();
                key.ImportFromPem(pem);
                keys[ComputeKeyId(key)] = key;
            }
            _keys = keys.Values.ToArray();
            _keyMap = keys;
        }

        /// <summary>Trusted keys keyed by their key_id (16 hex chars).</summary>
        public IReadOnlyDictionary<string, ECDsa> KeysById => _keyMap;

        /// <summary>Computes a key's key_id: first 16 hex chars of SHA-256
        /// over its DER SubjectPublicKeyInfo.</summary>
        public static string ComputeKeyId(ECDsa key)
        {
            byte[] spki = key.ExportSubjectPublicKeyInfo();
            byte[] digest = SHA256.HashData(spki);
            return Convert.ToHexString(digest)[..16].ToLowerInvariant();
        }

        /// <summary>
        /// Verifies a manifest signature the way vmcreate-iso signs it
        /// (<c>openssl dgst -sha256 -sign</c>: ECDSA P-256 over SHA-256, DER
        /// (Rfc3279DerSequence) signature encoding). The signature covers the
        /// EXACT manifest bytes — nothing is re-serialized.
        /// </summary>
        /// <returns>The trusted key that verified the signature, or null when
        /// no trusted key (or the manifest's key_id) accepts it.</returns>
        /// <exception cref="FormatException">The signature is not valid DER
        /// for the named key.</exception>
        public ECDsa? VerifyManifestSignature(byte[] manifestBytes, byte[] signature)
        {
            if (manifestBytes == null || manifestBytes.Length == 0) return null;
            if (signature == null || signature.Length == 0) return null;

            // The key_id names which trusted key must verify. It is read
            // before the signature check, but only ever used to pick from
            // the fixed trust store — never as a key itself.
            string? keyId = TryReadKeyId(manifestBytes);
            if (keyId == null)
                return null;

            if (!KeysById.TryGetValue(keyId, out ECDsa? key))
                return null;

            return key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence)
                ? key
                : null;
        }

        /// <summary>
        /// Reads the manifest's key_id from its JSON without trusting any
        /// other field: the signature has not been verified yet, so callers
        /// must not act on anything but this selector.
        /// </summary>
        private static string? TryReadKeyId(byte[] manifestBytes)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(manifestBytes);
                if (!doc.RootElement.TryGetProperty("key_id", out JsonElement keyIdEl)
                    || keyIdEl.ValueKind != JsonValueKind.String)
                    return null;
                string? keyId = keyIdEl.GetString();
                // Strictly 16 hex chars — the key_id is used to pick a key
                // from the trust store, never as a path or a key value.
                return keyId is { Length: 16 } && keyId.All(char.IsAsciiHexDigitLower)
                    ? keyId
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}