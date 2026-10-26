using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using VMCreate.CLI.Commands;

namespace VMCreate.Tests
{
    /// <summary>
    /// Pins the fail-loudly validation contract for
    /// <c>--static-ip</c>/<c>--gateway</c>/<c>--dns</c>: a typo must not
    /// silently deploy a guest nobody can reach. The prefix length is part
    /// of the address contract (it is the netmask deciding which
    /// destinations are on-link), so a bare address is rejected rather than
    /// guessed /24.
    /// </summary>
    [TestClass]
    public sealed class CliStaticNetworkValidationTests
    {
        private static CreateArgs Args(string? ip = null, string? gateway = null, string? dns = null) => new()
        {
            Image = "kali",
            StaticIp = ip,
            StaticGateway = gateway,
            StaticDns = dns,
        };

        private static (bool Valid, string? Ip, string? Gateway, string? Dns) Validate(CreateArgs args)
        {
            bool ok = CreateCommand.TryValidateStaticNetwork(args, jsonMode: false,
                out string? ip, out string? gateway, out string? dns);
            return (ok, ip, gateway, dns);
        }

        [TestMethod]
        public void Valid_FullStaticConfig_PassesThrough()
        {
            var (valid, ip, gateway, dns) = Validate(Args(
                "192.168.10.50/24", "192.168.10.1", "8.8.8.8,1.1.1.1"));

            Assert.IsTrue(valid);
            Assert.AreEqual("192.168.10.50/24", ip);
            Assert.AreEqual("192.168.10.1", gateway);
            Assert.AreEqual("8.8.8.8,1.1.1.1", dns);
        }

        [TestMethod]
        public void Valid_AddressOnly_GatewayAndDnsNull()
        {
            var (valid, ip, gateway, dns) = Validate(Args(" 10.0.0.5/16 "));

            Assert.IsTrue(valid);
            Assert.AreEqual("10.0.0.5/16", ip, "trimmed");
            Assert.IsNull(gateway);
            Assert.IsNull(dns);
        }

        [TestMethod]
        public void Valid_DhcpDefaults_AllNull()
        {
            var (valid, ip, gateway, dns) = Validate(Args());

            Assert.IsTrue(valid);
            Assert.IsNull(ip);
            Assert.IsNull(gateway);
            Assert.IsNull(dns);
        }

        [TestMethod]
        public void GatewayWithoutStaticIp_Fails()
        {
            var (valid, _, _, _) = Validate(Args(gateway: "192.168.10.1"));
            Assert.IsFalse(valid, "gateway is meaningless under DHCP");
        }

        [TestMethod]
        public void DnsWithoutStaticIp_Fails()
        {
            var (valid, _, _, _) = Validate(Args(dns: "8.8.8.8"));
            Assert.IsFalse(valid, "dns is meaningless under DHCP");
        }

        [TestMethod]
        [DataRow("192.168.10.50", DisplayName = "bare IP")]
        [DataRow("192.168.10.50/", DisplayName = "empty prefix")]
        [DataRow("192.168.10.50/abc", DisplayName = "non-numeric prefix")]
        [DataRow("192.168.10.50/0", DisplayName = "zero prefix")]
        [DataRow("192.168.10.50/33", DisplayName = "prefix over 32")]
        public void StaticIpWithoutExplicitPrefix_Fails(string ip)
        {
            var (valid, _, _, _) = Validate(Args(ip));
            Assert.IsFalse(valid, "the prefix length is the netmask — it must be explicit");
        }

        [TestMethod]
        [DataRow("2001:db8::1/64", DisplayName = "IPv6")]
        [DataRow("not-an-ip/24", DisplayName = "garbage")]
        [DataRow("192.168.10.50/24/8", DisplayName = "double slash")]
        public void StaticIpNotIpv4Cidr_Fails(string ip)
        {
            var (valid, _, _, _) = Validate(Args(ip));
            Assert.IsFalse(valid, "IPv4 only");
        }

        [TestMethod]
        [DataRow("2001:db8::1", DisplayName = "IPv6")]
        [DataRow("gateway", DisplayName = "garbage")]
        public void GatewayNotIpv4_Fails(string gateway)
        {
            var (valid, _, _, _) = Validate(Args("192.168.10.50/24", gateway));
            Assert.IsFalse(valid);
        }

        [TestMethod]
        [DataRow("8.8.8.8,junk", DisplayName = "one bad entry")]
        [DataRow("2001:db8::1", DisplayName = "IPv6")]
        public void DnsNotIpv4List_Fails(string dns)
        {
            var (valid, _, _, _) = Validate(Args("192.168.10.50/24", dns: dns));
            Assert.IsFalse(valid);
        }

        [TestMethod]
        public void DnsWhitespaceSeparated_NormalizesToCommas()
        {
            var (valid, _, _, dns) = Validate(Args("192.168.10.50/24", dns: " 8.8.8.8 , 1.1.1.1 "));
            Assert.IsTrue(valid);
            Assert.AreEqual("8.8.8.8,1.1.1.1", dns);
        }
    }
}