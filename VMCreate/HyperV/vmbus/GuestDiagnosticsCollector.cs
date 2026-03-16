using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    /// <summary>
    /// Collects diagnostic information from the ISO guest via native SSH.
    /// Used when the customization workflow stalls or fails — the host reaches
    /// into the guest, pulls logs, and reports back before force-stopping the VM.
    /// 
    /// Requirements (on the ISO guest):
    ///   - openssh-server running
    ///   - SSH public key injected via KVP (key-only auth)
    /// </summary>
    public class GuestDiagnosticsCollector : IGuestDiagnosticsCollector
    {
        private readonly ILogger<GuestDiagnosticsCollector> _logger;
        private const string GuestUsername = "ubuntu";
        private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(30);

        public GuestDiagnosticsCollector(ILogger<GuestDiagnosticsCollector> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Connects to the ISO guest via PowerShell Direct and collects autorun
        /// service status, journal output, mount state, and recent kernel messages.
        /// Returns a structured diagnostics string, or an error message if the
        /// connection itself fails.
        /// </summary>
        public async Task<GuestDiagnostics> CollectAsync(string vmName, CancellationToken ct, string privateKeyPath = null)
        {
            _logger.LogInformation("Collecting diagnostics from ISO guest via PowerShell Direct for VM: {VMName}", vmName);

            try
            {
                string output = await RunGuestCommandAsync(vmName, @"
                    echo '=== autorun service ==='
                    systemctl show autorun.service -p ExecMainStatus -p Result -p ActiveState -p SubState 2>&1
                    echo '=== autorun status ==='
                    systemctl status autorun.service --no-pager 2>&1
                    echo '=== journal (last 80 lines) ==='
                    sudo journalctl -u autorun.service --no-pager -n 80 2>&1
                    echo '=== mounts ==='
                    mount | grep /mnt 2>&1
                    echo '=== dmesg (last 20 lines) ==='
                    sudo dmesg | tail -20 2>&1
                ", ct, privateKeyPath);

                var diag = new GuestDiagnostics
                {
                    RawOutput = output,
                    Summary = ParseSummary(output),
                    CollectedSuccessfully = true
                };

                _logger.LogInformation("Guest diagnostics collected successfully. Summary: {Summary}", diag.Summary);
                return diag;
            }
            catch (OperationCanceledException)
            {
                throw; // Don't swallow cancellation
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to collect guest diagnostics via PowerShell Direct: {Message}", ex.Message);
                return new GuestDiagnostics
                {
                    RawOutput = ex.Message,
                    Summary = $"Could not reach ISO guest: {ex.Message}",
                    CollectedSuccessfully = false
                };
            }
        }

        /// <summary>
        /// Executes a command inside the guest VM via native ssh.exe.
        /// Discovers the VM's IP from Hyper-V, then connects with key-based auth.
        /// </summary>
        private async Task<string> RunGuestCommandAsync(string vmName, string linuxCommand, CancellationToken ct, string privateKeyPath = null)
        {
            if (string.IsNullOrEmpty(privateKeyPath) || !System.IO.File.Exists(privateKeyPath))
                throw new InvalidOperationException("SSH private key path is required for guest diagnostics collection.");

            // Discover the VM's IP address via Get-VMNetworkAdapter
            string vmIp = await DiscoverVmIpAsync(vmName, ct);
            if (string.IsNullOrEmpty(vmIp))
                throw new InvalidOperationException($"Could not discover IP address for VM '{vmName}'. Guest networking may not be ready.");

            _logger.LogDebug("Discovered VM IP {IP} for diagnostics on {VMName}", vmIp, vmName);

            // Normalize line endings for bash
            linuxCommand = linuxCommand.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            var args = new StringBuilder();
            args.Append($"-i \"{privateKeyPath}\" ");
            // Host key checking is intentionally disabled: we connect to freshly-created
            // local Hyper-V guests whose host keys are regenerated on every install.
            args.Append("-o StrictHostKeyChecking=no ");
            args.Append("-o BatchMode=yes ");
            args.Append("-o ConnectTimeout=10 ");
            args.Append("-o UserKnownHostsFile=NUL ");
            args.Append($"{GuestUsername}@{vmIp} ");
            args.Append($"bash -c {EscapeForSsh(linuxCommand)}");

            _logger.LogDebug("SSH diagnostics exec: ssh {Args}", args.ToString());

            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = args.ToString(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(SshTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"SSH diagnostics timed out after {SshTimeout.TotalSeconds}s — guest may not have sshd running or network is unreachable.");
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            string stdoutStr = stdout.ToString();
            string stderrStr = stderr.ToString();

            if (process.ExitCode != 0)
            {
                string errorDetail = !string.IsNullOrWhiteSpace(stderrStr) ? stderrStr.Trim() : stdoutStr.Trim();
                _logger.LogWarning("SSH diagnostics command exited with code {ExitCode}: {Error}", process.ExitCode, errorDetail);
                // Still return whatever output we got — partial diagnostics are better than none
                if (!string.IsNullOrWhiteSpace(stdoutStr))
                    return stdoutStr;
                throw new Exception($"SSH diagnostics failed (exit code {process.ExitCode}): {errorDetail}");
            }

            return stdoutStr;
        }

        /// <summary>
        /// Discovers the VM's IPv4 address via Get-VMNetworkAdapter.
        /// </summary>
        private async Task<string> DiscoverVmIpAsync(string vmName, CancellationToken ct)
        {
            using var ps = PowerShell.Create();
            ps.AddScript($@"
                $adapters = Get-VMNetworkAdapter -VMName '{vmName.Replace("'", "''")}' -ErrorAction SilentlyContinue
                foreach ($a in $adapters) {{
                    foreach ($ip in $a.IPAddresses) {{
                        if ($ip -match '^\d+\.\d+\.\d+\.\d+$') {{
                            $ip
                            return
                        }}
                    }}
                }}
            ");

            var result = await Task.Run(() => ps.Invoke(), ct);
            return result.FirstOrDefault()?.ToString();
        }

        private static string EscapeForSsh(string command)
        {
            string escaped = command.Replace("'", "'\\''");
            return $"'{escaped}'";
        }

        /// <summary>
        /// Extracts a human-readable one-line summary from the raw diagnostics output.
        /// Focuses on the autorun service result and exit code.
        /// </summary>
        private static string ParseSummary(string rawOutput)
        {
            if (string.IsNullOrWhiteSpace(rawOutput))
                return "No output from guest.";

            string result = null;
            string exitStatus = null;
            string activeState = null;

            foreach (string line in rawOutput.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("Result=")) result = trimmed.Substring("Result=".Length);
                else if (trimmed.StartsWith("ExecMainStatus=")) exitStatus = trimmed.Substring("ExecMainStatus=".Length);
                else if (trimmed.StartsWith("ActiveState=")) activeState = trimmed.Substring("ActiveState=".Length);
            }

            if (result != null && result != "success")
                return $"autorun.service failed (Result={result}, ExitCode={exitStatus ?? "?"}, State={activeState ?? "?"})";

            if (result == "success" && activeState == "inactive")
                return "autorun.service completed successfully but VM did not shut down (OnSuccess=poweroff.target may not have fired)";

            if (activeState == "activating" || activeState == "active")
                return $"autorun.service is still running (State={activeState})";

            if (result == null)
                return "Could not determine autorun.service status from guest output.";

            return $"autorun.service: Result={result}, ExitCode={exitStatus ?? "?"}, State={activeState ?? "?"}";
        }
    }

    /// <summary>
    /// Diagnostic data collected from the ISO guest.
    /// </summary>
    public class GuestDiagnostics
    {
        /// <summary>Full raw output from the guest commands.</summary>
        public string RawOutput { get; set; }

        /// <summary>Human-readable one-line summary (shown in the UI phase card).</summary>
        public string Summary { get; set; }

        /// <summary>True if the PS Direct connection succeeded and data was collected.</summary>
        public bool CollectedSuccessfully { get; set; }
    }
}
