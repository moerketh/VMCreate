using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VMCreate.Tests.Downloaders
{
    /// <summary>
    /// Tests for the vmcreate-iso trust anchor: key_id derivation (same
    /// rule as the ISO repo), built-in keys, and signature verification
    /// over the exact manifest bytes. Test keys are created inside each
    /// test via the internal constructor — no test key ships.
    /// <para>
    /// One fixture test pins the REAL v2.1.0 manifest and signature
    /// against the built-in key: it proves .NET accepts exactly what
    /// openssl signed.
    /// </para>
    /// </summary>
    [TestClass]
    public class IsoReleaseTrustTests
    {
        private const string FixtureDir = "Downloaders/Fixtures";

        [TestMethod]
        public void ComputeKeyId_MatchesIsoRepoRule()
        {
            // The ISO repo names keys by the first 16 hex chars of
            // sha256(SubjectPublicKeyInfo DER). The two built-in keys must
            // derive exactly those ids — the file names in keys/.
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string keyId = IsoReleaseTrust.ComputeKeyId(key);

            // Independent recomputation of the same rule.
            byte[] spki = key.ExportSubjectPublicKeyInfo();
            string expected = Convert.ToHexString(SHA256.HashData(spki))[..16].ToLowerInvariant();
            Assert.AreEqual(expected, keyId);
            StringAssert.Matches(keyId, new System.Text.RegularExpressions.Regex("^[0-9a-f]{16}$"));
        }

        [TestMethod]
        public void BuiltIn_TrustStore_HoldsTheIsoSigningKeys()
        {
            Assert.IsTrue(IsoReleaseTrust.BuiltIn.KeysById.ContainsKey("354c516cfd23698f"),
                "the primary vmcreate-iso signing key must be built in");
            Assert.IsTrue(IsoReleaseTrust.BuiltIn.KeysById.ContainsKey("957fc58ae68b029c"),
                "the secondary vmcreate-iso signing key must be built in");
            Assert.AreEqual(2, IsoReleaseTrust.BuiltIn.KeysById.Count);
        }

        [TestMethod]
        public void VerifyManifestSignature_ValidSignature_ReturnsKey()
        {
            using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pem = signingKey.ExportSubjectPublicKeyInfoPem();
            var trust = new IsoReleaseTrust(pem);
            byte[] manifest = Encoding.UTF8.GetBytes(
                SampleManifest.Replace("\"354c516cfd23698f\"", $"\"{IsoReleaseTrust.ComputeKeyId(signingKey)}\""));
            byte[] signature = signingKey.SignData(manifest, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);

            var key = trust.VerifyManifestSignature(manifest, signature);

            Assert.IsNotNull(key, "a manifest signed by a trusted key must verify");
            Assert.AreEqual(IsoReleaseTrust.ComputeKeyId(signingKey),
                IsoReleaseTrust.ComputeKeyId(key!));
        }

        [TestMethod]
        public void VerifyManifestSignature_TamperedManifest_Fails()
        {
            using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trust = new IsoReleaseTrust(signingKey.ExportSubjectPublicKeyInfoPem());
            byte[] manifest = Encoding.UTF8.GetBytes(SampleManifest);
            byte[] signature = signingKey.SignData(manifest, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            // A single flipped byte after the key_id line: the signature
            // covers the exact bytes, so this must not verify.
            byte[] tampered = Encoding.UTF8.GetBytes(
                SampleManifest.Replace("\"2.1.0\"", "\"2.2.0\""));

            Assert.IsNull(trust.VerifyManifestSignature(tampered, signature));
        }

        [TestMethod]
        public void VerifyManifestSignature_TamperedSignature_Fails()
        {
            using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trust = new IsoReleaseTrust(signingKey.ExportSubjectPublicKeyInfoPem());
            byte[] manifest = Encoding.UTF8.GetBytes(SampleManifest);
            byte[] signature = signingKey.SignData(manifest, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            signature[signature.Length - 1] ^= 0xFF;

            Assert.IsNull(trust.VerifyManifestSignature(manifest, signature));
        }

        [TestMethod]
        public void VerifyManifestSignature_UnknownKeyId_Fails()
        {
            using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trust = new IsoReleaseTrust(signingKey.ExportSubjectPublicKeyInfoPem());
            byte[] manifest = Encoding.UTF8.GetBytes(
                SampleManifest.Replace("\"354c516cfd23698f\"", "\"0011223344556677\""));
            byte[] signature = signingKey.SignData(manifest, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);

            // The key_id names a key the trust store does not hold, so no
            // trusted key exists to verify with — even though the bytes
            // are genuinely signed by some key.
            Assert.IsNull(trust.VerifyManifestSignature(manifest, signature));
        }

        [TestMethod]
        public void VerifyManifestSignature_MalformedKeyId_Fails()
        {
            using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trust = new IsoReleaseTrust(signingKey.ExportSubjectPublicKeyInfoPem());
            // Path-shaped and wrong-length key_ids must never be usable.
            byte[] manifest = Encoding.UTF8.GetBytes(
                SampleManifest.Replace("\"354c516cfd23698f\"", "\"../../etc/passwd\""));
            byte[] signature = signingKey.SignData(manifest, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);

            Assert.IsNull(trust.VerifyManifestSignature(manifest, signature));
        }

        [TestMethod]
        public void VerifyManifestSignature_NotJson_Fails()
        {
            using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trust = new IsoReleaseTrust(signingKey.ExportSubjectPublicKeyInfoPem());
            byte[] manifest = Encoding.UTF8.GetBytes("not json at all");
            byte[] signature = signingKey.SignData(manifest, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);

            Assert.IsNull(trust.VerifyManifestSignature(manifest, signature));
        }

        [TestMethod]
        public void VerifyManifestSignature_EmptyInputs_Fail()
        {
            var trust = new IsoReleaseTrust(ECDsa.Create(ECCurve.NamedCurves.nistP256)
                .ExportSubjectPublicKeyInfoPem());
            Assert.IsNull(trust.VerifyManifestSignature(Array.Empty<byte>(), new byte[72]));
            Assert.IsNull(trust.VerifyManifestSignature(Encoding.UTF8.GetBytes(SampleManifest), Array.Empty<byte>()));
        }

        /// <summary>
        /// The real vmcreate-iso v2.1.0 manifest and signature against the
        /// BUILT-IN key: proves .NET accepts what openssl signed
        /// (<c>release/make-manifest.sh</c> in the ISO repo).
        /// </summary>
        [TestMethod]
        public void VerifyManifestSignature_RealV210Fixture_VerifiesWithBuiltInKey()
        {
            string fixtureDir = Path.Combine(AppContext.BaseDirectory, FixtureDir);
            byte[] manifestBytes = File.ReadAllBytes(
                Path.Combine(fixtureDir, "vmcreate-2.1.0.manifest.json"));
            byte[] signature = File.ReadAllBytes(
                Path.Combine(fixtureDir, "vmcreate-2.1.0.manifest.json.sig"));

            var key = IsoReleaseTrust.BuiltIn.VerifyManifestSignature(manifestBytes, signature);

            Assert.IsNotNull(key,
                "the published v2.1.0 manifest signature must verify against the built-in key");
            using var doc = JsonDocument.Parse(manifestBytes);
            Assert.AreEqual("354c516cfd23698f", doc.RootElement.GetProperty("key_id").GetString());
        }

        private const string SampleManifest = """
            {
              "schema": 1,
              "name": "vmcreate-iso",
              "version": "2.1.0",
              "tag": "v2.1.0",
              "commit": "62484c1a14fe7e0133c7e8da5924419e3476a1de",
              "iso_file": "vmcreate-2.1.0.iso",
              "iso_size": 86165504,
              "iso_sha256": "3fab5ec511a073edc9bd06b3def01ce08b70699fdd81224b305808b82660813b",
              "key_id": "354c516cfd23698f"
            }
            """;
    }
}