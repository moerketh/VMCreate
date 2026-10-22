using System;
using System.Collections.Generic;
using System.IO;

namespace VMCreate
{
    /// <summary>
    /// Where an <see cref="SshGuestShell"/> connects: either the guest's
    /// discovered IPv4 address (post-boot path over the temporary NIC) or the
    /// VM's Hyper-V socket channel (ISO boot-cycle path — no NIC, no IP).
    /// </summary>
    /// <remarks>
    /// The transport treats a target as opaque connection arguments: how the
    /// host string is reached (TCP to an IP vs hvc.exe relaying over AF_HYPERV)
    /// is entirely inside <see cref="SshArguments"/>. Retry semantics differ by
    /// kind: an IP target re-discovers its address on transport errors (a DHCP
    /// lease can change mid-pipeline); a vsock target has a stable host string
    /// (the VM GUID), so transport errors retry without any re-discovery.
    /// </remarks>
    public sealed class SshConnectionTarget
    {
        /// <summary>Enum discriminator; kept public for test readability.</summary>
        public enum TargetKind
        {
            /// <summary>TCP to the guest's IPv4.</summary>
            IpAddress,

            /// <summary>Hyper-V socket: host string is the VM GUID, reached
            /// via an hvc.exe ProxyCommand.</summary>
            Vsock,
        }

        /// <summary>The ssh destination host string: an IPv4 address or a VM GUID.</summary>
        public string Host { get; }

        public TargetKind Kind { get; }

        /// <summary>
        /// Extra ssh arguments for this target as distinct argv elements.
        /// For vsock, the ProxyCommand invoking hvc.exe by ABSOLUTE path —
        /// ssh expands %-tokens in ProxyCommand values itself, so the path
        /// must already be expanded (never pass a literal %SystemRoot%
        /// through; ssh would read it as the token %S). Elements are passed
        /// to <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>,
        /// so they reach ssh.exe verbatim; any quotes are ssh's own
        /// ProxyCommand syntax, not Windows command-line quoting.
        /// </summary>
        public IReadOnlyList<string> ExtraArguments { get; }

        private SshConnectionTarget(string host, TargetKind kind, IReadOnlyList<string> extraArguments)
        {
            Host = host;
            Kind = kind;
            ExtraArguments = extraArguments;
        }

        public static SshConnectionTarget ForIp(string ipAddress) =>
            new(ipAddress, TargetKind.IpAddress, Array.Empty<string>());

        /// <param name="vmGuid">VM's Hyper-V GUID — the string hvc.exe accepts
        /// (GUIDs are unique where VM names can be duplicated or contain
        /// spaces).</param>
        public static SshConnectionTarget ForVsock(string vmGuid)
        {
            if (!File.Exists(HvcExePath))
            {
                throw new NotSupportedException(
                    $"The vsock SSH transport requires hvc.exe at '{HvcExePath}', which is not present on this " +
                    "host. hvc.exe ships with Hyper-V's management tools (enable the Hyper-V PowerShell/RSAT " +
                    "components). Without it there is no supported host-side connector to the guest's vsock listener.");
            }
            return new(vmGuid, TargetKind.Vsock, BuildVsockArguments());
        }

        /// <summary>
        /// hvc.exe path — expanded here, once. Absolute path for the same
        /// anti-hijack reason as <see cref="SshTransport.SshExePath"/>: a bare
        /// "hvc" resolves via PATH. Windows ships hvc.exe with Hyper-V exactly
        /// here.
        /// </summary>
        public static string HvcExePath =>
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\hvc.exe");

        private static IReadOnlyList<string> BuildVsockArguments()
        {
            // %h is ssh's own token for the destination host (the VM GUID).
            // The element embeds the hvc.exe path in ssh's ProxyCommand quote
            // syntax — with ArgumentList delivery the quotes and backslashes
            // reach ssh.exe verbatim (live-verified end-to-end against a
            // booting ISO guest).
            string hvc = HvcExePath;
            return new[] { "-o", $"ProxyCommand=\"{hvc}\" nc -t vsock %h 22" };
        }
    }
}
