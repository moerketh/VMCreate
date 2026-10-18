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

        public IGuestShell CreateForWindows(string vmName, string username, string password)
        {
            return _psDirectFactory.Create(vmName, username, password);
        }
    }
}
