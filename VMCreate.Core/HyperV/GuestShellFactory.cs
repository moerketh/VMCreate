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
        {
            return new SshGuestShell(_logger, vmName, privateKeyPath);
        }

        public IGuestShell CreateForWindows(string vmName, string username, string password)
        {
            return _psDirectFactory.Create(vmName, username, password);
        }
    }
}
