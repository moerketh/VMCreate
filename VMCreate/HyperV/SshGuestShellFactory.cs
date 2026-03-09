using Microsoft.Extensions.Logging;

namespace VMCreate
{
    /// <summary>
    /// Creates <see cref="SshGuestShell"/> instances with runtime parameters.
    /// Registered in DI to avoid direct <c>new SshGuestShell()</c> calls.
    /// </summary>
    public class SshGuestShellFactory : IGuestShellFactory
    {
        private readonly ILogger _logger;

        public SshGuestShellFactory(ILogger<SshGuestShellFactory> logger)
        {
            _logger = logger;
        }

        public IGuestShell Create(string vmName, string privateKeyPath)
        {
            return new SshGuestShell(_logger, vmName, privateKeyPath);
        }
    }
}
