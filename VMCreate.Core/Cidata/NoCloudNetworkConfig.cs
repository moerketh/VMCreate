using System;
using System.Linq;
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

    /// <summary>
    /// MAC address of the VM's primary network adapter, in any of the common
    /// forms ("00155D01173E" as Hyper-V reports it, or "00:15:5d:01:17:3e").
    /// The rendered <c>network-config</c> matches the interface by MAC so the
    /// static address lands on exactly that NIC — deploy plumbing NICs (the
    /// Gen-1 cloning cycle's "VMCreate Temp" adapter, also hv_netvsc) are
    /// never matched, on any renderer that supports match rules.
    /// </summary>
    public string? PrimaryNicMacAddress { get; init; }

    /// <summary>
    /// The single construction point for deploy code: null under DHCP, a
    /// validated and normalized instance otherwise. Validation lives here so
    /// the CLI, the seed renderers and the strategies cannot drift apart on
    /// what a "valid" static address is. Throws <see cref="ArgumentException"/>
    /// with the same message the CLI surfaces.
    /// </summary>
    public static StaticNetworkConfig? Create(
        string? staticIp, string? staticGateway, string? staticDns, string? primaryNicMacAddress = null)
    {
        if (!NoCloudNetworkConfig.TryParseCidrIpv4(staticIp, out string? ip))
        {
            if (string.IsNullOrWhiteSpace(staticIp))
                return null; // DHCP
            throw NoCloudNetworkConfig.InvalidCidr(staticIp);
        }

        string? gateway = null;
        if (!string.IsNullOrWhiteSpace(staticGateway))
        {
            if (!NoCloudNetworkConfig.TryParseIpv4(staticGateway, out string? parsedGateway))
                throw NoCloudNetworkConfig.InvalidIpv4(staticGateway, "gateway");
            gateway = parsedGateway;
        }

        string? dns = null;
        if (!string.IsNullOrWhiteSpace(staticDns))
        {
            string[] entries = staticDns.Split(new[] { ',', ' ', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string entry in entries)
            {
                if (!NoCloudNetworkConfig.TryParseIpv4(entry, out _))
                    throw NoCloudNetworkConfig.InvalidIpv4(entry, "dns entry");
            }
            dns = string.Join(",", entries);
        }

        return new StaticNetworkConfig
        {
            StaticIp = ip,
            StaticGateway = gateway,
            StaticDns = dns,
            PrimaryNicMacAddress = NoCloudNetworkConfig.NormalizeMacAddress(primaryNicMacAddress),
        };
    }
}

/// <summary>
/// Renders a cloud-init NoCloud <c>network-config</c> (network-config format
/// version 2) for static guest addressing. Written into the seed ISO next to
/// <c>user-data</c>/<c>meta-data</c>; cloud-init applies it on first boot,
/// before any <c>user-data</c> runs.
/// </summary>
/// <remarks>
/// Interface selection matches the PRIMARY adapter's MAC address
/// (<c>match: {macaddress: …}</c>) rather than a name or a driver: the NoCloud
/// seed path never runs the cloning ISO's interface-renaming fixups (so a
/// bare-<c>eth0</c> id would never apply on an image naming its NIC
/// <c>ens*</c>/<c>enp*</c>), and a driver match would catch EVERY
/// <c>hv_netvsc</c> NIC — on a Gen-1 source the cloning cycle's temporary
/// deploy adapter is attached while the seed is consumed, and a driver match
/// would hand it the same static address on the wrong switch. The MAC matches
/// exactly one NIC by construction. cloud-init resolves the MAC to the
/// interface's current name, so the config follows the device whatever it is
/// called. Renderers without match support fall back to the id, which stays
/// <c>eth0</c> — the name Hyper-V cloud images use.
/// The default route uses the modern
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
        if (string.IsNullOrWhiteSpace(config.PrimaryNicMacAddress))
            throw new InvalidOperationException(
                "Static addressing requires the primary network adapter's MAC address (see " +
                $"{nameof(StaticNetworkConfig.PrimaryNicMacAddress)}): the network-config matches the interface " +
                "by MAC so the address never lands on a deploy-plumbing NIC. Strategies fetch it from Hyper-V " +
                "when they build the seed.");
        // Normalized at the render boundary too: Create normalizes, but a
        // library caller can set the property directly, and netplan expects
        // lowercase colon-separated (its own guidance is to quote it).
        string mac = NormalizeMacAddress(config.PrimaryNicMacAddress)!;

        var sb = new StringBuilder();
        sb.Append("version: 2\nethernets:\n  eth0:\n");
        sb.Append("    match:\n      macaddress: \"").Append(mac).Append("\"\n");
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

    /// <summary>
    /// Strict dotted-quad IPv4 parse. <see cref="System.Net.IPAddress.TryParse"/>
    /// is NOT used: it happily accepts the shorthand forms <c>192.168.1</c>,
    /// <c>10</c> and <c>0x0a000001</c>, none of which netplan accepts — a
    /// shorthand value would sail through CLI validation, land in the
    /// network-config, and leave the guest with no address (and the pinned-IP
    /// SSH wait timing out against a host like "192.168.1").
    /// </summary>
    internal static bool TryParseIpv4(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0 || normalized.Length > 15)
            return false;

        string[] octets = normalized.Split('.');
        if (octets.Length != 4)
            return false;
        foreach (string octet in octets)
        {
            // Exactly 1-3 ASCII digits, no signs, no leading zeros ("01" is
            // rejected like a strict parser — unambiguous and consistent).
            if (octet.Length is < 1 or > 3)
                return false;
            int val = 0;
            foreach (char c in octet)
            {
                if (c < '0' || c > '9')
                    return false;
                val = val * 10 + (c - '0');
            }
            if (val > 255 || (octet.Length > 1 && octet[0] == '0'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Strict CIDR parse: strict dotted-quad host plus a plain-decimal prefix
    /// 1-32. <c>int.TryParse</c> alone would accept "/ 24"; the digit loop
    /// rejects any non-digit (and any sign or whitespace).
    /// </summary>
    internal static bool TryParseCidrIpv4(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim();
        string[] parts = normalized.Split('/', 2);
        if (parts.Length != 2 || !TryParseIpv4(parts[0], out _))
            return false;

        string prefix = parts[1];
        if (prefix.Length is < 1 or > 2)
            return false;
        int val = 0;
        foreach (char c in prefix)
        {
            if (c < '0' || c > '9')
                return false;
            val = val * 10 + (c - '0');
        }
        return val is >= 1 and <= 32;
    }

    internal static ArgumentException InvalidCidr(string? value) => new(
        $"Static IP '{value}' must be an IPv4 address in CIDR form with an explicit prefix length, e.g. 192.168.10.50/24 " +
        "(the prefix is the netmask that decides which destinations the guest treats as on-link).",
        nameof(StaticNetworkConfig.StaticIp));

    internal static ArgumentException InvalidIpv4(string? value, string what) => new(
        $"'{value}' is not a valid IPv4 {what}; expected four dotted-decimal octets (e.g. 192.168.10.1).",
        nameof(StaticNetworkConfig.StaticIp));

    /// <summary>
    /// Normalizes a MAC address to netplan's lowercase colon-separated form
    /// ("00155D01173E" and "00-15-5D-01-17-3E" both become
    /// "00:15:5d:01:17:3e"), or null when absent. Anything that is not 12
    /// hex digits (after separator removal) throws — a malformed MAC would
    /// match no interface and silently leave the guest offline.
    /// </summary>
    internal static string? NormalizeMacAddress(string? value)
    {
        string digits = (value ?? string.Empty)
            .Replace(":", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Trim();
        if (digits.Length == 0)
            return null;
        if (digits.Length != 12 || digits.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException(
                $"'{value}' is not a valid MAC address; expected 12 hex digits, optionally colon- or dash-separated.",
                nameof(StaticNetworkConfig.PrimaryNicMacAddress));

        return string.Concat(Enumerable.Range(0, 6)
            .Select(i => digits.Substring(i * 2, 2).ToLowerInvariant() + (i < 5 ? ":" : "")));
    }

    private static string RequireCidrIpv4(string? value, string paramName)
        => TryParseCidrIpv4(value, out string normalized)
            ? normalized
            : throw InvalidCidr(value);

    private static string RequireIpv4(string? value, string paramName)
        => TryParseIpv4(value, out string normalized)
            ? normalized
            : throw InvalidIpv4(value, "address");

    private static string[] SplitNameservers(string value)
    {
        string[] entries = value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string entry in entries)
        {
            if (!TryParseIpv4(entry, out _))
                throw InvalidIpv4(entry, "dns entry");
        }
        if (entries.Length == 0)
            throw new ArgumentException("At least one nameserver is required.", nameof(StaticNetworkConfig.StaticDns));
        return entries;
    }
}