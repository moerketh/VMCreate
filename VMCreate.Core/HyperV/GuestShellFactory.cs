using System;
using Microsoft.Extensions.Logging;

namespace VMCreate
{
    /// <summary>
    /// Creates <see cref="IGuestShell"/> instances with runtime parameters, selecting the
    /// appropriate transport per guest OS: SSH for Linux VMs (<see cref="Create"/>) and
    /// PowerShell Direct for Windows VMs (<see cref="CreateForWindows"/>).
    /// Registered in DI to avoid direct <c>new</c> calls on the shell implementations.
    /// </summary>
    public class GuestShellFactory : IGuestShellFactory
    {
        private readonly ILogger _logger;
        private readonly PowerShellDirectGuestShellFactory _psDirectFactory;

        public GuestShellFactory(ILogger<GuestShellFactory> logger, PowerShellDirectGuestShellFactory psDirectFactory)
        {
            _logger = logger;
            _psDirectFactory = psDirectFactory ?? throw new ArgumentNullException(nameof(psDirectFactory));
        }

        public IGuestShell Create(string vmName, string privateKeyPath)
            => new SshGuestShell(_logger, vmName, privateKeyPath);

        public IGuestShell Create(string vmName, string privateKeyPath, string username)
            => new SshGuestShell(_logger, vmName, privateKeyPath, username);

        /// <summary>
        /// Creates a shell that connects over the Hyper-V socket channel
        /// (no NIC, no IP): the ISO boot-cycle path. The caller resolves the
        /// VM GUID (e.g. via <c>KvpHostToGuest.ResolveVmGuidAnyState</c>) —
        /// GUIDs, unlike names, are unique and space-free, which is what the
        /// hvc.exe ProxyCommand needs as its host string.
        /// </summary>
        public IGuestShell CreateVsock(string vmName, string privateKeyPath, string vmGuid)
        {
            if (string.IsNullOrWhiteSpace(vmGuid))
                throw new ArgumentException("VM GUID is required for the vsock shell.", nameof(vmGuid));
            return new SshGuestShell(_logger, vmName, privateKeyPath, "vmcreate", SshConnectionTarget.ForVsock(vmGuid));
        }

        /// <summary>
        /// Creates a shell that connects to a FIXED guest IPv4 — the
        /// static-IP deploy path (CLI --static-ip). The guest is configured
        /// with this address via the NoCloud seed; Hyper-V's IP discovery
        /// leans on DHCP leases and may never see it, so the shell never
        /// discovers and never re-discovers: the address is pinned for the
        /// shell's lifetime. Like the vsock shell, transport errors do not
        /// trigger address re-discovery (the address cannot change).
        /// </summary>
        public IGuestShell CreateWithPinnedIp(string vmName, string privateKeyPath, string username, string staticIp)
        {
            if (string.IsNullOrWhiteSpace(staticIp))
                throw new ArgumentException("A static IP is required for the pinned-IP shell.", nameof(staticIp));
            return new SshGuestShell(_logger, vmName, privateKeyPath, username, SshConnectionTarget.ForIp(staticIp));
        }

        public IGuestShell CreateForWindows(string vmName, string username, string password)
        {
            return _psDirectFactory.Create(vmName, username, password);
        }
    }
}
