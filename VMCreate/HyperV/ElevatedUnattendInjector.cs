using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Spawns the same executable elevated (UAC prompt) with the
    /// <c>--inject-unattend</c> flag so that <see cref="UnattendInjector"/>
    /// runs with a full-Administrator token. The GUI itself stays un-elevated.
    /// </summary>
    public class ElevatedUnattendInjector : IUnattendInjector
    {
        private readonly ILogger<ElevatedUnattendInjector> _logger;

        public ElevatedUnattendInjector(ILogger<ElevatedUnattendInjector> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> InjectAsync(string vhdxPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Spawning elevated child to inject unattend.xml into {VhdxPath}", vhdxPath);

            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--inject-unattend \"{vhdxPath}\""
            };

            Process process;
            try
            {
                process = Process.Start(psi);
                if (process == null)
                {
                    _logger.LogError("Process.Start returned null for elevated child");
                    return false;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED — the user declined the UAC prompt
                _logger.LogWarning("User declined UAC prompt for unattend injection");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start elevated child process for unattend injection");
                return false;
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Unattend injection cancelled while waiting for elevated child");
                try { process.Kill(); } catch { /* best effort */ }
                return false;
            }

            bool success = process.ExitCode == 0;
            if (success)
                _logger.LogInformation("Elevated unattend injection succeeded for {VhdxPath}", vhdxPath);
            else
                _logger.LogError("Elevated unattend injection failed (exit code {ExitCode}) for {VhdxPath}", process.ExitCode, vhdxPath);

            return success;
        }
    }
}