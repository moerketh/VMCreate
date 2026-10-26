using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using VMCreate.Cidata;

namespace VMCreate.Tests.Cidata
{
    /// <summary>
    /// Pins the NoCloud <c>network-config</c> (v2) rendering for static
    /// guest addressing: cloud-init applies this on first boot, before any
    /// user-data, so a wrong shape silently strands the guest with no
    /// network — these tests keep the exact YAML contract loud.
    /// </summary>
    [TestClass]
    public sealed class NoCloudNetworkConfigTests
    {
        private static StaticNetworkConfig Config(
            string ip = "192.168.10.50/24", string? gateway = null, string? dns = null) => new()
        {
            StaticIp = ip,
            StaticGateway = gateway,
            StaticDns = dns,
        };

        [TestMethod]
        public void Render_FullConfig_UsesV2Eth0WithDefaultRouteAndDns()
        {
            string yaml = NoCloudNetworkConfig.Render(Config(gateway: "192.168.10.1", dns: "8.8.8.8,1.1.1.1"));

            StringAssert.Contains(yaml, "version: 2");
            StringAssert.Contains(yaml, "ethernets:");
            StringAssert.Contains(yaml, "  eth0:");
            StringAssert.Contains(yaml, "    addresses: [192.168.10.50/24]");
            StringAssert.Contains(yaml, "    routes:");
            StringAssert.Contains(yaml, "      - to: default");
            StringAssert.Contains(yaml, "        via: 192.168.10.1");
            StringAssert.Contains(yaml, "    nameservers:");
            StringAssert.Contains(yaml, "      addresses: [8.8.8.8, 1.1.1.1]");
        }

        [TestMethod]
        public void Render_AddressOnly_OmitsRouteAndNameservers()
        {
            // --static-ip with neither --gateway nor --dns: the guest gets the
            // address but no default route and no explicit resolvers.
            string yaml = NoCloudNetworkConfig.Render(Config());

            StringAssert.Contains(yaml, "    addresses: [192.168.10.50/24]");
            Assert.IsFalse(yaml.Contains("routes:"), "no gateway given — no route section");
            Assert.IsFalse(yaml.Contains("nameservers:"), "no dns given — no nameserver section");
        }

        [TestMethod]
        public void Render_NoDeprecatedGateway4()
        {
            // The v2 subset's gateway4 is deprecated; the renderer must use
            // the modern routes form exclusively.
            string yaml = NoCloudNetworkConfig.Render(Config(gateway: "192.168.10.1"));
            Assert.IsFalse(yaml.Contains("gateway4"), "gateway4 is deprecated netplan syntax");
        }

        [TestMethod]
        public void Render_NoMatchBlock_TheIdIsTheInterfaceName()
        {
            // Without match:, netplan treats the id as the literal interface
            // name — the contract that binds the config to the Hyper-V NIC
            // VMCreate's renaming fixups call eth0. A match block would
            // re-introduce MAC/driver coupling the deploy cannot promise.
            string yaml = NoCloudNetworkConfig.Render(Config());
            Assert.IsFalse(yaml.Contains("match:"), "no match block — the id is the interface name");
        }

        [TestMethod]
        public void Render_GatewayOnly_OmitsNameservers()
        {
            string yaml = NoCloudNetworkConfig.Render(Config(gateway: "192.168.10.1"));

            StringAssert.Contains(yaml, "        via: 192.168.10.1");
            Assert.IsFalse(yaml.Contains("nameservers:"), "dns not requested");
        }

        [TestMethod]
        public void Render_TrimsWhitespaceSeparatedDnsEntries()
        {
            string yaml = NoCloudNetworkConfig.Render(Config(dns: " 8.8.8.8 , 1.1.1.1 "));
            StringAssert.Contains(yaml, "      addresses: [8.8.8.8, 1.1.1.1]");
        }

        [TestMethod]
        public void IsRequested_TrueOnlyWhenStaticIpPresent()
        {
            Assert.IsTrue(NoCloudNetworkConfig.IsRequested(Config()));
            Assert.IsFalse(NoCloudNetworkConfig.IsRequested(new StaticNetworkConfig { StaticGateway = "192.168.10.1" }),
                "gateway alone is not static addressing");
            Assert.IsFalse(NoCloudNetworkConfig.IsRequested(null));
        }

        [TestMethod]
        [DataRow(null, DisplayName = "null IP")]
        [DataRow("", DisplayName = "empty IP")]
        [DataRow("   ", DisplayName = "whitespace IP")]
        public void Render_MissingIp_Throws(string? ip)
        {
            Assert.Throws<ArgumentException>(() =>
                NoCloudNetworkConfig.Render(new StaticNetworkConfig { StaticIp = ip! }));
        }

        [TestMethod]
        [DataRow("192.168.10.50", DisplayName = "bare IP")]
        [DataRow("192.168.10.50/", DisplayName = "empty prefix")]
        [DataRow("192.168.10.50/abc", DisplayName = "non-numeric prefix")]
        [DataRow("192.168.10.50/0", DisplayName = "zero prefix")]
        [DataRow("192.168.10.50/33", DisplayName = "prefix over 32")]
        [DataRow("192.168.10.50/24/8", DisplayName = "double slash")]
        public void Render_IpWithoutExplicitPrefix_Throws(string ip)
        {
            // The prefix is the netmask deciding which destinations are
            // on-link; guessing it would mis-configure every non-/24 subnet.
            var ex = Assert.Throws<ArgumentException>(() =>
                NoCloudNetworkConfig.Render(Config(ip: ip)));
            StringAssert.Contains(ex.Message, "CIDR");
        }

        [TestMethod]
        [DataRow("2001:db8::1/64", DisplayName = "IPv6")]
        [DataRow("not-an-ip/24", DisplayName = "garbage")]
        [DataRow("999.1.1.1/24", DisplayName = "out-of-range octets")]
        public void Render_NonIpv4Address_Throws(string ip)
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                NoCloudNetworkConfig.Render(Config(ip: ip)));
            Assert.IsTrue(ex.Message.Contains("IPv4") || ex.Message.Contains("not a valid"),
                $"message should name the problem: {ex.Message}");
        }

        [TestMethod]
        public void Render_GatewayNotIpv4_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                NoCloudNetworkConfig.Render(Config(gateway: "2001:db8::1")));
            StringAssert.Contains(ex.Message, "IPv4");
        }

        [TestMethod]
        public void Render_DnsNotIpv4_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                NoCloudNetworkConfig.Render(Config(dns: "8.8.8.8,junk")));
        }

        [TestMethod]
        public void Render_DnsOnlyWhitespace_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                NoCloudNetworkConfig.Render(Config(dns: " , ")));
        }
    }
}