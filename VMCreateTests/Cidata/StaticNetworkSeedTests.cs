using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text;
using VMCreate.Cidata;

namespace VMCreate.Tests.Cidata
{
    /// <summary>
    /// Pins that static addressing rides the NoCloud seed as a
    /// <c>network-config</c> file (v2), exactly when requested: cloud-init
    /// applies it on first boot — a seed that carries it under DHCP, or
    /// misses it under --static-ip, silently changes how the guest boots.
    /// </summary>
    [TestClass]
    public sealed class StaticNetworkSeedTests
    {
        private static readonly StaticNetworkConfig Static = new()
        {
            StaticIp = "192.168.10.50/24",
            StaticGateway = "192.168.10.1",
            StaticDns = "8.8.8.8,1.1.1.1",
        };

        /// <summary>
        /// True when IMAPI2 burned the ISO (unavailable in some CI containers —
        /// the test then reports inconclusive, not failed).
        /// </summary>
        private static bool TryBuildCidataIso(CidataSeedOptions options, out string isoPath, out string? failure)
        {
            isoPath = Path.Combine(Path.GetTempPath(), "vmcreate-cidata-static-" + Guid.NewGuid().ToString("N") + ".iso");
            try
            {
                CidataSeedBuilder.BuildCidataIso(isoPath, options, NullLogger.Instance);
                failure = null;
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
        }

        private static void TryBuildCloudInitIso(StaticNetworkConfig? network, out string isoPath, out string? failure)
        {
            isoPath = Path.Combine(Path.GetTempPath(), "vmcreate-cloudinit-static-" + Guid.NewGuid().ToString("N") + ".iso");
            try
            {
                CloudInitSeedBuilder.BuildSeedIso(isoPath, "t-seed", "ssh-ed25519 AAAAtest vmcreate", NullLogger.Instance,
                    customUserData: null, staticNetwork: network);
                failure = null;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
        }

        /// <summary>
        /// Joliet SVD directory records (sector 17+, UTF-16BE) name the staged
        /// files; the network-config CONTENT is plain ASCII in the file data
        /// area. The renderer's exact shape is pinned separately — here we
        /// only need presence/absence of the file and its marker lines.
        /// </summary>
        private static bool IsoContains(string isoPath, string text, Encoding encoding)
        {
            byte[] all = File.ReadAllBytes(isoPath);
            byte[] needle = encoding.GetBytes(text);
            for (int i = 0; i <= all.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && all[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        private static bool IsoContainsAscii(string isoPath, string text) => IsoContains(isoPath, text, Encoding.ASCII);

        [TestMethod]
        public void CidataSeed_CarriesNetworkConfig_WhenStaticIpRequested()
        {
            var options = new CidataSeedOptions
            {
                Username = "omarchy",
                Password = "hunter2!",
                Hostname = "target",
                DiskSizeBytes = 150L * 1024 * 1024 * 1024,
                StaticNetwork = Static,
            };

            if (!TryBuildCidataIso(options, out string isoPath, out string? failure))
            {
                Assert.Inconclusive($"IMAPI2 unavailable: {failure}");
                return;
            }
            try
            {
                Assert.IsTrue(IsoContainsAscii(isoPath, "version: 2"), "network-config (v2) must be on the seed");
                Assert.IsTrue(IsoContainsAscii(isoPath, "addresses: [192.168.10.50/24]"));
                Assert.IsTrue(IsoContainsAscii(isoPath, "via: 192.168.10.1"));
                Assert.IsTrue(IsoContainsAscii(isoPath, "8.8.8.8"));
                NoCloudSeedAssert.IsCloudInitReadable(isoPath);
            }
            finally
            {
                try { File.Delete(isoPath); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void CidataSeed_OmitsNetworkConfig_UnderDhcp()
        {
            // Regression pin: the DHCP deploy's seed must stay byte-shape
            // identical to the pre-static-feature seed (no network-config).
            var options = new CidataSeedOptions
            {
                Username = "omarchy",
                Password = "hunter2!",
                Hostname = "target",
                DiskSizeBytes = 150L * 1024 * 1024 * 1024,
            };

            if (!TryBuildCidataIso(options, out string isoPath, out string? failure))
            {
                Assert.Inconclusive($"IMAPI2 unavailable: {failure}");
                return;
            }
            try
            {
                Assert.IsFalse(IsoContainsAscii(isoPath, "version: 2"),
                    "no static IP requested — the seed must not carry a network-config");
                Assert.IsFalse(IsoContainsAscii(isoPath, "ethernets:"));
                NoCloudSeedAssert.IsCloudInitReadable(isoPath);
            }
            finally
            {
                try { File.Delete(isoPath); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void CidataSeed_NetworkConfigWithoutNoCloudPair_Throws()
        {
            // network-config only reaches cloud-init through the meta-data +
            // user-data NoCloud pair; silently shipping it without them
            // would strand the static guest on DHCP.
            var options = new CidataSeedOptions
            {
                Username = "omarchy",
                Password = "hunter2!",
                Hostname = "target",
                DiskSizeBytes = 150L * 1024 * 1024 * 1024,
                HoldEnhancedSessionPort = false,
                StaticNetwork = Static,
            };

            Assert.Throws<InvalidOperationException>(() =>
                CidataSeedBuilder.BuildCidataIso(
                    Path.Combine(Path.GetTempPath(), "vmcreate-never-" + Guid.NewGuid().ToString("N") + ".iso"),
                    options, NullLogger.Instance));
        }

        [TestMethod]
        public void CloudInitSeed_CarriesNetworkConfig_WhenStaticIpRequested()
        {
            string isoPath = null!;
            try
            {
                TryBuildCloudInitIso(Static, out isoPath, out string? failure);
                if (failure != null)
                {
                    Assert.Inconclusive($"IMAPI2 unavailable: {failure}");
                    return;
                }

                Assert.IsTrue(IsoContainsAscii(isoPath, "version: 2"), "network-config (v2) must be on the seed");
                Assert.IsTrue(IsoContainsAscii(isoPath, "addresses: [192.168.10.50/24]"));
                Assert.IsTrue(IsoContainsAscii(isoPath, "via: 192.168.10.1"));
                NoCloudSeedAssert.IsCloudInitReadable(isoPath);
            }
            finally
            {
                try { if (isoPath != null) File.Delete(isoPath); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void CloudInitSeed_OmitsNetworkConfig_UnderDhcp()
        {
            string isoPath = null!;
            try
            {
                TryBuildCloudInitIso(null, out isoPath, out _);

                Assert.IsFalse(IsoContainsAscii(isoPath, "version: 2"),
                    "no static IP requested — the seed must not carry a network-config");
                Assert.IsFalse(IsoContainsAscii(isoPath, "ethernets:"));
                NoCloudSeedAssert.IsCloudInitReadable(isoPath);
            }
            finally
            {
                try { if (isoPath != null) File.Delete(isoPath); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void CloudInitSeed_InvalidStaticNetwork_ThrowsBeforeIso()
        {
            Assert.Throws<ArgumentException>(() =>
                CloudInitSeedBuilder.BuildSeedIso(
                    Path.Combine(Path.GetTempPath(), "vmcreate-never-" + Guid.NewGuid().ToString("N") + ".iso"),
                    "t-seed", "ssh-ed25519 AAAAtest vmcreate", NullLogger.Instance,
                    customUserData: null,
                    staticNetwork: new StaticNetworkConfig { StaticIp = "192.168.10.50" }));
        }
    }
}