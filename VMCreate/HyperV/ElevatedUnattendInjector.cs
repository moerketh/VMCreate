using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV.Unattend;

namespace VMCreate
{
    /// <summary>
    /// Spawns the current executable elevated with the <c>--inject-unattend</c> switch
    /// and delegates the actual injection to <see cref="UnattendInjector"/...gt;.
    /// </summary>
    public class ElevatedUnattendInjector : IUnattendInjector
    {
        private readonly UnattendInjector _injector;
        private readonly ILogger<ElevatedUnattendInjector> _logger;

        public ElevatedUnattendInjector(UnattendInjector injector, ILogger<ElevatedUnattendInjector> logger)
        {
            _injector = injector ?? throw new ArgumentNullException(nameof(injector));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> InjectAsync(string vhdxPath, CancellationToken cancellationToken)
        {
            if (!File.Exists(vhdxPath))
            {
                _logger.LogError("VHDX not found for unattend injection: {VhdxPath}", vhdxPath);
                return false;
            }

            if (IsCurrentProcessElevated())
            {
                _logger.LogInformation("Already elevated; performing injection in-process for {VhdxPath}", vhdxPath);
                return await _injector.InjectAsync(vhdxPath, cancellationToken);
            }

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
                try { process.Kill(); } catch { }
                return false;
            }

            bool success = process.ExitCode == 0;
            if (success)
                _logger.LogInformation("Elevated unattend injection succeeded for {VhdxPath}", vhdxPath);
            else
                _logger.LogError("Elevated unattend injection failed (exit code {ExitCode}) for {VhdxPath}", process.ExitCode, vhdxPath);

            return success;
        }

        private static bool IsCurrentProcessElevated()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
