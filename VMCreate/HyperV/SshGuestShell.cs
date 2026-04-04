using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Implements <see cref="IGuestShell"/> using native ssh.exe over the network.
    /// Discovers the VM's IP via Hyper-V WMI, polls for SSH readiness, then
    /// executes commands and transfers files using key-based authentication.
    /// 
    /// Only requires standard sshd on the guest — no pwsh or PowerShell remoting needed.
    /// </summary>
    public class SshGuestShell : IGuestShell
    {
        private readonly ILogger _logger;
        private readonly string _privateKeyPath;
        private string _vmIpAddress;

        private const string AutomationUser = "vmcreate";
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(180);
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(120);
        private const int MaxRetries = 3;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

        public string VmName { get; }

        public SshGuestShell(ILogger logger, string vmName, string privateKeyPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            VmName = vmName ?? throw new ArgumentNullException(nameof(vmName));
            _privateKeyPath = privateKeyPath ?? throw new ArgumentNullException(nameof(privateKeyPath));
        }

        // ── Connection lifecycle ─────────────────────────────────────────

        /// <summary>
        /// Waits until native SSH can successfully connect to the VM.
        /// Discovers the VM's IP via Get-VMNetworkAdapter, then tests SSH connectivity.
        /// Retries every 5 seconds until the guest's SSH server is ready.
        /// </summary>
        public async Task WaitForReadyAsync(CancellationToken ct)
        {
            _logger.LogInformation("Waiting for SSH to become available on VM {VMName}...", VmName);

            var deadline = DateTime.UtcNow + ReadyTimeout;
            Exception lastError = null;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(_vmIpAddress))
                {
                    _vmIpAddress = await DiscoverVmIpAsync(ct);
                    if (string.IsNullOrEmpty(_vmIpAddress))
                    {
                        _logger.LogDebug("VM IP not yet available, retrying...");
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                        continue;
                    }
                    _logger.LogInformation("Discovered VM IP: {IpAddress}", _vmIpAddress);
                }

                try
                {
                    string result = await RunCommandInternalAsync("echo 'ssh-ready'", TimeSpan.FromSeconds(15), ct);
                    if (result != null && result.Contains("ssh-ready"))
                    {
                        _logger.LogInformation("SSH is ready on VM {VMName} ({IP})", VmName, _vmIpAddress);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.LogDebug("SSH not ready yet: {Message}", ex.Message);
                    if (ex.Message.Contains("refused") || ex.Message.Contains("unreachable")
                        || ex.Message.Contains("No route") || ex.Message.Contains("timed out"))
                        _vmIpAddress = null;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }

            throw new TimeoutException(
                $"SSH did not become available on VM '{VmName}' within {ReadyTimeout.TotalSeconds}s. " +
                $"Last error: {lastError?.Message}");
        }

        /// <summary>
        /// Sends a shutdown command to the guest. Tolerates the expected SSH transport
        /// error that occurs when the remote OS shuts down mid-session.
        /// </summary>
        public async Task ShutdownGuestAsync(CancellationToken ct)
        {
            try
            {
                await RunCommandInternalAsync("sudo shutdown -h now", TimeSpan.FromSeconds(15), ct);
            }
            catch (Exception ex) when (IsSshTransportError(ex))
            {
                _logger.LogDebug("SSH session ended as expected during shutdown: {Message}", ex.Message);
            }
        }

        // ── IGuestShell implementation ───────────────────────────────────

        /// <inheritdoc/>
        public async Task<string> RunCommandAsync(string bashCommand, CancellationToken ct)
        {
            return await RunWithRetryAsync(bashCommand, CommandTimeout, ct);
        }

        /// <inheritdoc/>
        public async Task CopyContentAsync(string content, string guestPath, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            string base64 = Convert.ToBase64String(bytes);
            string safePath = EscapeSingleQuotes(guestPath);

            string command = $@"
                sudo mkdir -p ""$(dirname '{safePath}')""
                echo '{base64}' | base64 -d | sudo tee '{safePath}' > /dev/null
                sudo chmod 644 '{safePath}'
            ";

            await RunCommandAsync(command, ct);
            _logger.LogInformation("Wrote content -> {GuestPath} on VM {VMName}", guestPath, VmName);
        }

        /// <inheritdoc/>
        public async Task CopyFileAsync(string hostPath, string guestPath, CancellationToken ct)
        {
            if (!File.Exists(hostPath))
                throw new FileNotFoundException($"Host file not found: {hostPath}");

            byte[] content = await File.ReadAllBytesAsync(hostPath, ct);
            string base64 = Convert.ToBase64String(content);
            string safePath = EscapeSingleQuotes(guestPath);

            string command = $@"
                sudo mkdir -p ""$(dirname '{safePath}')""
                echo '{base64}' | base64 -d | sudo tee '{safePath}' > /dev/null
                sudo chmod 644 '{safePath}'
            ";

            await RunCommandAsync(command, ct);
            _logger.LogInformation("Copied {HostPath} -> {GuestPath} on VM {VMName}", hostPath, guestPath, VmName);
        }

        // ── Private helpers ──────────────────────────────────────────────

        private async Task<string> DiscoverVmIpAsync(CancellationToken ct)
        {
            using var ps = PowerShell.Create();
            ps.AddScript($@"
                $adapters = Get-VMNetworkAdapter -VMName '{VmName.Replace("'", "''")}' -ErrorAction SilentlyContinue
                # Prefer the temporary adapter added by VMCreate for post-boot SSH
                $sorted = $adapters | Sort-Object {{ if ($_.Name -eq 'VMCreate Temp') {{ 0 }} else {{ 1 }} }}
                foreach ($a in $sorted) {{
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

        private async Task<string> RunWithRetryAsync(string linuxCommand, TimeSpan timeout, CancellationToken ct)
        {
            Exception lastEx = null;
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await RunCommandInternalAsync(linuxCommand, timeout, ct);
                }
                catch (Exception ex) when (attempt < MaxRetries && IsSshTransportError(ex))
                {
                    lastEx = ex;
                    _logger.LogWarning("SSH transport error on attempt {Attempt}/{Max}, retrying in {Delay}s: {Message}",
                        attempt, MaxRetries, RetryDelay.TotalSeconds, ex.Message);
                    _vmIpAddress = null;
                    await Task.Delay(RetryDelay, ct);
                    _vmIpAddress = await DiscoverVmIpAsync(ct);
                }
            }
            throw lastEx!;
        }

        private static bool IsSshTransportError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                string msg = e.Message;
                if (msg.Contains("SSH transport process has abruptly terminated")
                    || msg.Contains("SSH client session has ended")
                    || msg.Contains("remote session to break")
                    || msg.Contains("broken pipe", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("closed by remote host", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("Connection reset", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private async Task<string> RunCommandInternalAsync(string linuxCommand, TimeSpan timeout, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_vmIpAddress))
                throw new InvalidOperationException("VM IP address not discovered yet. Call WaitForReadyAsync first.");

            // Normalize Windows CRLF → LF so bash doesn't choke on \r
            linuxCommand = linuxCommand.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            var args = new StringBuilder();
            args.Append($"-i \"{_privateKeyPath}\" ");
            // Host key checking is intentionally disabled: we connect to freshly-created
            // local Hyper-V guests whose host keys are regenerated on every install.
            args.Append("-o StrictHostKeyChecking=no ");
            args.Append("-o BatchMode=yes ");
            args.Append("-o ConnectTimeout=10 ");
            args.Append("-o UserKnownHostsFile=NUL ");
            args.Append($"{AutomationUser}@{_vmIpAddress} ");
            args.Append($"bash -c {EscapeForSsh(linuxCommand)}");

            _logger.LogDebug("SSH exec: ssh {Args}", args.ToString());

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
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"SSH command timed out after {timeout.TotalSeconds}s on VM '{VmName}'");
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
                // Filter out the benign "Permanently added" SSH warning before choosing the error detail
                string significantStderr = string.Join("\n", stderrStr
                    .Split('\n')
                    .Where(line => !string.IsNullOrWhiteSpace(line)
                                && !line.Contains("Permanently added", StringComparison.OrdinalIgnoreCase)))
                    .Trim();
                string errorDetail = !string.IsNullOrEmpty(significantStderr) ? significantStderr : stdoutStr.Trim();
                throw new Exception(
                    $"SSH command failed (exit code {process.ExitCode}) on VM '{VmName}': {errorDetail}");
            }

            // Log non-trivial stderr (filter out the expected "Permanently added" known-hosts warning)
            if (!string.IsNullOrWhiteSpace(stderrStr))
            {
                var significantLines = stderrStr
                    .Split('\n')
                    .Where(line => !string.IsNullOrWhiteSpace(line)
                                && !line.Contains("Permanently added", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (significantLines.Length > 0)
                    _logger.LogDebug("SSH stderr (non-fatal): {Stderr}", string.Join("\n", significantLines).Trim());
            }

            return stdoutStr;
        }

        /// <summary>
        /// Escapes a value for safe embedding inside a single-quoted bash string.
        /// Closes the quote, inserts an escaped literal quote, and re-opens the quote.
        /// </summary>
        private static string EscapeSingleQuotes(string value) =>
            value.Replace("'", "'\\''");

        private static string EscapeForSsh(string command)
        {
            string escaped = command.Replace("'", "'\\''");
            return $"'{escaped}'";
        }
    }
}
