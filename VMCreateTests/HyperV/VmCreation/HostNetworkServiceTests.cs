using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using VMCreate.HyperV.VmCreation;

namespace VMCreate.Tests.HyperV.VmCreation
{
    [TestClass]
    public sealed class HostNetworkServiceTests
    {
        [TestMethod]
        public void ResolveHostDnsServers_ReturnsNonEmptyStringOrNull()
        {
            var service = new HostNetworkService();
            string result = service.ResolveHostDnsServers();

            if (!string.IsNullOrEmpty(result))
            {
                Assert.IsTrue(result.Contains("."), "Expected IPv4 DNS addresses to contain dots");
                Assert.IsFalse(result.Contains(":"), "Expected only IPv4 addresses, not IPv6");
            }
        }

        [TestMethod]
        public void ResolveHostDnsServers_ReturnsCommaSeparatedIPv4Addresses()
        {
            var service = new HostNetworkService();
            string result = service.ResolveHostDnsServers();

            if (string.IsNullOrEmpty(result))
                return;

            string[] parts = result.Split(',');
            foreach (string part in parts)
            {
                Assert.IsTrue(System.Net.IPAddress.TryParse(part.Trim(), out var address), $"Could not parse '{part}' as an IP address");
                Assert.AreEqual(System.Net.Sockets.AddressFamily.InterNetwork, address.AddressFamily, $"Expected IPv4, got: {address}");
            }
        }
    }
}
