using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VMCreate.Cidata;

/// <summary>
/// Static addressing for a NoCloud seed: the guest's IPv4 (CIDR, required),
/// an optional default-route gateway and optional nameservers. Carried as a
/// separate type so both seed builders (<see cref="CloudInitSeedBuilder"/>,
/// <see cref="CidataSeedBuilder"/>) render the exact same
/// <c>network-config</c> from one implementation.
/// </summary>
public sealed class StaticNetworkConfig
{
    /// <summary>
    /// Guest IPv4 in CIDR form, e.g. <c>192.168.10.50/24</c>. The prefix is
    /// part of the address contract — it is the netmask that decides which
    /// destinations the guest treats as on-link — so a bare address is a
    /// configuration error, not a /24 guess.
    /// </summary>
    public string StaticIp { get; init; } = null!;

    /// <summary>Optional default-route gateway IPv4. Null omits the default route.</summary>
    public string? StaticGateway { get; init; }

    /// <summary>Optional nameserver IPv4s, comma- or whitespace-separated.</summary>
    public string? StaticDns { get; init; }
}

/// <summary>
/// Renders a cloud-init NoCloud <c>network-config</c> (network-config format
/// version 2) for static guest addressing. Written into the seed ISO next to
/// <c>user-data</c>/<c>meta-data</c>; cloud-init applies it on first boot,
/// before any <c>user-data</c> runs.
/// </summary>
/// <remarks>
/// Interface id <c>eth0</c> with no <c>match:</c> block: per netplan (and
/// cloud-init's v2 subset), the id then IS the interface name. That matches
/// the Hyper-V <c>hv_netvsc</c> NIC after VMCreate's interface-renaming
/// fixups, and it stays stable across deploys (the MAC does not — it is
/// host-allocated). The default route uses the modern
/// <c>routes: [{to: default, via: …}]</c> form; cloud-init normalizes
/// <c>to: default</c> to <c>0.0.0.0/0</c> and the networkd renderer emits a
/// <c>[Route]</c> section, so no deprecated <c>gateway4</c> is written.
/// IPv4 only.
/// </remarks>
public static class NoCloudNetworkConfig
{
    /// <summary>
    /// Renders the v2 network-config. The interface gets the static address;
    /// the default route and nameservers appear only when the corresponding
    /// input is present.
    /// </summary>
    public static string Render(StaticNetworkConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string ip = RequireCidrIpv4(config.StaticIp, nameof(config.StaticIp));
        string? gateway = null;
        if (!string.IsNullOrWhiteSpace(config.StaticGateway))
            gateway = RequireIpv4(config.StaticGateway, nameof(config.StaticGateway));

        var sb = new StringBuilder();
        sb.Append("version: 2\nethernets:\n  eth0:\n");
        sb.Append($"    addresses: [{ip}]\n");
        if (gateway is not null)
        {
            sb.Append("    routes:\n");
            sb.Append("      - to: default\n");
            sb.Append($"        via: {gateway}\n");
        }
        if (!string.IsNullOrWhiteSpace(config.StaticDns))
        {
            string[] nameservers = SplitNameservers(config.StaticDns);
            sb.Append("    nameservers:\n");
            sb.Append($"      addresses: [{string.Join(", ", nameservers)}]\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="config"/> requests static addressing (the
    /// seed builders write <c>network-config</c> only then).
    /// </summary>
    public static bool IsRequested(StaticNetworkConfig? config)
        => config is not null && !string.IsNullOrWhiteSpace(config.StaticIp);

    private static string RequireCidrIpv4(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A static IP in CIDR form (e.g. 192.168.10.50/24) is required.", paramName);

        string[] parts = value.Split('/', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out int prefix) || prefix < 1 || prefix > 32)
            throw new ArgumentException(
                $"Static IP '{value}' must be an IPv4 address in CIDR form with an explicit prefix length, e.g. 192.168.10.50/24 " +
                "(the prefix is the netmask that decides which destinations the guest treats as on-link).",
                paramName);

        RequireIpv4(parts[0], paramName);
        return value.Trim();
    }

    private static string RequireIpv4(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !IPAddress.TryParse(value.Trim(), out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"'{value}' is not a valid IPv4 address.", paramName);
        }
        return value.Trim();
    }

    private static string[] SplitNameservers(string value)
    {
        string[] entries = value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string entry in entries)
            RequireIpv4(entry, nameof(StaticNetworkConfig.StaticDns));
        if (entries.Length == 0)
            throw new ArgumentException("At least one nameserver is required.", nameof(StaticNetworkConfig.StaticDns));
        return entries;
    }
}