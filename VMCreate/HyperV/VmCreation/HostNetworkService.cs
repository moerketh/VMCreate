using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Resolves DNS nameserver information from the Windows host.
    /// </summary>
    public interface IHostNetworkService
    {
        /// <summary>
        /// Returns a comma-separated list of IPv4 DNS server addresses from active
        /// network interfaces, or null if none can be determined.
        /// </summary>
        string ResolveHostDnsServers();
    }

    internal class HostNetworkService : IHostNetworkService
    {
        public string ResolveHostDnsServers()
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                            && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var gatewayInterfaces = interfaces
                .Where(ni => ni.GetIPProperties().GatewayAddresses
                    .Any(ga => ga.Address.AddressFamily == AddressFamily.InterNetwork))
                .ToList();

            var targetInterfaces = gatewayInterfaces.Any() ? gatewayInterfaces : interfaces;

            var dnsAddresses = targetInterfaces
                .SelectMany(ni => ni.GetIPProperties().DnsAddresses)
                .Where(addr => addr.AddressFamily == AddressFamily.InterNetwork)
                .Select(addr => addr.ToString())
                .Distinct()
                .ToList();

            return dnsAddresses.Any() ? string.Join(",", dnsAddresses) : null;
        }
    }
}
