using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text.Json;

namespace VMCreate.Tests
{
    /// <summary>
    /// In-memory ISecretProtector — the store tests must not depend on DPAPI
    /// (which would encrypt under the test-runner account and make fixture
    /// files machine/user-bound).
    /// </summary>
    internal sealed class PlainSecretProtector : ISecretProtector
    {
        public string? Protect(string? plaintext) => plaintext;
        public string? Unprotect(string? protectedValue) => protectedValue;
    }

    [TestClass]
    public class UserPreferencesStoreTests
    {
        private static string TempPath() =>
            Path.Combine(Path.GetTempPath(), "vmcreate-tests", $"settings-{Guid.NewGuid():N}.json");

        private static UserPreferencesStore CreateStore(string path, ISecretProtector? protector = null)
        {
            var settings = Options.Create(new AppSettings { UserPreferencesPath = path });
            return new UserPreferencesStore(
                protector ?? new PlainSecretProtector(),
                NullLogger<UserPreferencesStore>.Instance,
                settings);
        }

        [TestMethod]
        public void Load_MissingFile_ReturnsDefaults()
        {
            var store = CreateStore(TempPath());

            var prefs = store.Load();

            Assert.AreEqual(DnsMode.Host, prefs.DnsMode);
            Assert.AreEqual(RdpBackend.Auto, prefs.RdpBackend);
            Assert.IsNull(prefs.HtbApiToken);
            Assert.AreEqual(4096, prefs.DefaultMemoryInMB);
            Assert.AreEqual(2, prefs.DefaultCpuCount);
            Assert.AreEqual(150, prefs.DefaultDiskSizeInGB);
        }

        [TestMethod]
        public void SaveThenLoad_RoundTripsAllFields()
        {
            var path = TempPath();
            var store = CreateStore(path);

            store.Save(new UserPreferences
            {
                HtbApiToken = "htb-token-123",
                LlmChatCompletionsUrl = "https://llm.local/v1/chat/completions",
                LlmApiKey = "sk-test",
                LlmModel = "gpt-test",
                DnsMode = DnsMode.Custom,
                CustomNameservers = "9.9.9.9,1.1.1.1",
                RdpBackend = RdpBackend.Lamco,
                DefaultMemoryInMB = 8192,
                DefaultCpuCount = 4,
                DefaultDiskSizeInGB = 200,
                QemuImgPathOverride = @"D:\tools\qemu-img.exe",
                ExtractPathOverride = @"D:\tmp",
            });

            var loaded = CreateStore(path).Load();

            Assert.AreEqual("htb-token-123", loaded.HtbApiToken);
            Assert.AreEqual("https://llm.local/v1/chat/completions", loaded.LlmChatCompletionsUrl);
            Assert.AreEqual("sk-test", loaded.LlmApiKey);
            Assert.AreEqual("gpt-test", loaded.LlmModel);
            Assert.AreEqual(DnsMode.Custom, loaded.DnsMode);
            Assert.AreEqual("9.9.9.9,1.1.1.1", loaded.CustomNameservers);
            Assert.AreEqual(RdpBackend.Lamco, loaded.RdpBackend);
            Assert.AreEqual(8192, loaded.DefaultMemoryInMB);
            Assert.AreEqual(4, loaded.DefaultCpuCount);
            Assert.AreEqual(200, loaded.DefaultDiskSizeInGB);
            Assert.AreEqual(@"D:\tools\qemu-img.exe", loaded.QemuImgPathOverride);
            Assert.AreEqual(@"D:\tmp", loaded.ExtractPathOverride);
        }

        [TestMethod]
        public void Save_ProtectsSecretsWithProtector()
        {
            var path = TempPath();
            var store = CreateStore(path, new PrefixProtector());
            store.Save(new UserPreferences { HtbApiToken = "secret-token", LlmApiKey = "secret-key" });

            // Raw file must never contain plaintext secrets.
            var raw = File.ReadAllText(path);
            StringAssert.DoesNotMatch(raw, new System.Text.RegularExpressions.Regex("secret-token"));
            StringAssert.DoesNotMatch(raw, new System.Text.RegularExpressions.Regex("secret-key"));
        }

        [TestMethod]
        public void Load_CorruptFile_ReturnsDefaults()
        {
            var path = TempPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not valid json !!");

            var prefs = CreateStore(path).Load();

            Assert.AreEqual(RdpBackend.Auto, prefs.RdpBackend);
        }

        [TestMethod]
        public void Save_IsAtomic_NoTempFileLeftBehind()
        {
            var path = TempPath();
            var store = CreateStore(path);
            store.Save(new UserPreferences());

            Assert.IsFalse(File.Exists(path + ".tmp"));
            Assert.IsTrue(File.Exists(path));
        }

        [TestMethod]
        public void HasLlmConfiguration_RequiresUrlModelAndKey()
        {
            var prefs = new UserPreferences
            {
                LlmChatCompletionsUrl = "https://x/v1/chat/completions",
                LlmModel = "m",
                LlmApiKey = "k",
            };
            Assert.IsTrue(prefs.HasLlmConfiguration);

            prefs.LlmApiKey = null;
            Assert.IsFalse(prefs.HasLlmConfiguration);
        }

        [TestMethod]
        public void WithMaskedSecrets_NeverExposesFullSecrets()
        {
            var prefs = new UserPreferences { HtbApiToken = "0123456789abcdef", LlmApiKey = "sk-verylongkey" };

            var masked = prefs.WithMaskedSecrets();

            StringAssert.DoesNotMatch(masked.HtbApiToken!, new System.Text.RegularExpressions.Regex("012345"));
            StringAssert.DoesNotMatch(masked.LlmApiKey!, new System.Text.RegularExpressions.Regex("verylong"));
        }

        private sealed class PrefixProtector : ISecretProtector
        {
            public string? Protect(string? plaintext) =>
                plaintext == null ? null : "prot:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
            public string? Unprotect(string? protectedValue) =>
                protectedValue == null || !protectedValue.StartsWith("prot:", StringComparison.Ordinal)
                    ? protectedValue
                    : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["prot:".Length..]));
        }
    }

    [TestClass]
    public class SecretMaskTests
    {
        [TestMethod]
        public void Mask_LongSecret_KeepsOnlyLastFour()
        {
            Assert.AreEqual("••••••••cdef", SecretMask.Mask("0123456789abcdef"));
        }

        [TestMethod]
        public void Mask_ShortSecret_AllBullets()
        {
            Assert.AreEqual("•••", SecretMask.Mask("abc"));
        }

        [TestMethod]
        public void Mask_NullEmpty_Passthrough()
        {
            Assert.IsNull(SecretMask.Mask(null));
            Assert.AreEqual("", SecretMask.Mask(""));
        }
    }

    [TestClass]
    public class DpapiSecretProtectorTests
    {
        [TestMethod]
        public void ProtectUnprotect_RoundTripsUnderCurrentUser()
        {
            var protector = new DpapiSecretProtector();

            var protectedValue = protector.Protect("secret-value");
            var roundtrip = protector.Unprotect(protectedValue);

            StringAssert.StartsWith(protectedValue!, DpapiSecretProtector.ProtectedPrefix);
            Assert.AreNotEqual("secret-value", protectedValue);
            Assert.AreEqual("secret-value", roundtrip);
        }

        [TestMethod]
        public void Unprotect_PlaintextPassthrough_LegacyMigration()
        {
            Assert.AreEqual("legacy", new DpapiSecretProtector().Unprotect("legacy"));
        }

        [TestMethod]
        public void Unprotect_Garbage_ReturnsNull()
        {
            Assert.IsNull(new DpapiSecretProtector().Unprotect("dpapi:not-base64!!!"));
        }
    }
}
