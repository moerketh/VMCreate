using System;
using Microsoft.Extensions.Logging;

namespace VMCreate
{
    /// <summary>
    /// Creates <see cref="PowerShellDirectGuestShell"/> instances for Windows VMs.
    /// PowerShell Direct uses VMBus (not network) to communicate with the guest,
    /// so it works even without network connectivity.
    /// </summary>
    public class PowerShellDirectGuestShellFactory
    {
        private readonly ILogger<PowerShellDirectGuestShellFactory> _logger;

        public PowerShellDirectGuestShellFactory(ILogger<PowerShellDirectGuestShellFactory> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Creates a PowerShell Direct guest shell for the specified Windows VM.
        /// </summary>
        /// <param name="vmName">The Hyper-V VM name.</param>
        /// <param name="username">The Windows username for PowerShell Direct authentication.</param>
        /// <param name="password">The Windows password for PowerShell Direct authentication.</param>
        public IGuestShell Create(string vmName, string username, string password)
        {
            _logger.LogInformation("Creating PowerShell Direct guest shell for VM {VMName}", vmName);
            return new PowerShellDirectGuestShell(_logger, vmName, username, password);
        }
    }
}