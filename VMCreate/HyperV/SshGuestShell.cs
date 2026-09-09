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
        // 2026-08-29: 180s proved too tight for the FIRST boot of a freshly
        // cloned distro — a Parrot rollout took ~6.5 min from ISO-cycle
        // shutdown to sshd answering (btrfs first-boot initialization, cold
        // disk cache), and the deployment aborted ~20s before the guest came
        // up. 10 minutes matches the ISO boot-cycle's own shutdown budget
        // (ShutdownTimeoutSeconds) and PowerShell Direct's CommandTimeout;
        // the wait still polls IP + SSH every ~5s, so a fast guest connect
        // is detected immediately.
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(600);
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
        public async Task<string> RunCommandAsync(string command, CancellationToken ct)
        {
            return await RunWithRetryAsync(command, CommandTimeout, ct);
        }

        /// <inheritdoc/>
        public async Task<string> RunCommandAsync(string command, TimeSpan timeout, CancellationToken ct)
        {
            return await RunWithRetryAsync(command, timeout, ct);
        }

        // Windows CreateProcess command lines are capped at 32,767 chars.
        // Staying well below that leaves room for the ssh arguments plus the
        // bash-level quote escaping applied by EscapeForSsh (which can inflate
        // the string further). Exceeding the cap kills Process.Start with
        // Win32Exception 206 ("filename or extension is too long").
        private const int MaxCommandLength = 24 * 1024;

        // Base64 chunk length for the chunked copy path: far below
        // MaxCommandLength, large enough that a 36 KB install script
        // transfers in four ssh round trips.
        private const int CopyChunkBase64Length = 12 * 1024;

        /// <inheritdoc/>
        public async Task CopyContentAsync(string content, string guestPath, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            await CopyBase64ToGuestAsync(Convert.ToBase64String(bytes), guestPath, "644", ct);
            _logger.LogInformation("Wrote content -> {GuestPath} on VM {VMName}", guestPath, VmName);
        }

        /// <inheritdoc/>
        public async Task CopySecretAsync(string content, string guestPath, CancellationToken ct)
        {
            // Key material (VPN configs embedding client certs/private keys,
            // TLS keys, credentials): root:root 0600. The old 644-for-everything
            // contract left VPN private keys world-readable in /etc/openvpn/client/.
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            await CopyBase64ToGuestAsync(Convert.ToBase64String(bytes), guestPath, "600", ct);
            _logger.LogInformation("Wrote secret content -> {GuestPath} on VM {VMName} (root:root 0600)", guestPath, VmName);
        }

        /// <inheritdoc/>
        public async Task CopyFileAsync(string hostPath, string guestPath, CancellationToken ct)
        {
            if (!File.Exists(hostPath))
                throw new FileNotFoundException($"Host file not found: {hostPath}");

            byte[] content = await File.ReadAllBytesAsync(hostPath, ct);
            await CopyBase64ToGuestAsync(Convert.ToBase64String(content), guestPath, "644", ct);
            _logger.LogInformation("Copied {HostPath} -> {GuestPath} on VM {VMName}", hostPath, guestPath, VmName);
        }

        /// <summary>
        /// Transfers base64-encoded payload to the guest in chunks, then decodes
        /// it into place. The previous single-command variant embedded the whole
        /// base64 payload on the ssh command line; scripts past ~24 KB blew past
        /// the CreateProcess limit and failed with Win32Exception 206 before ssh
        /// ever ran. Chunking keeps every invocation small; the temp file makes
        /// the transfer atomic (decode only after all chunks landed).
        /// </summary>
        /// <remarks>
        /// Idempotency: the append chunks (printf ... >>) are NOT retried
        /// individually — a transport error after the write landed would
        /// duplicate the chunk's data in the temp file on retry. The chunk
        /// loop runs on the no-retry path; any transport failure restarts
        /// the WHOLE copy from a fresh temp file, which is always safe.
        /// </remarks>
        private async Task CopyBase64ToGuestAsync(string base64, string guestPath, string chmodMode, CancellationToken ct)
        {
            string safePath = EscapeSingleQuotes(guestPath);
            // Host-side dirname: the previous $@"sudo mkdir -p ""$(dirname
            // '{safePath}')""..." embedded real double quotes in a verbatim
            // string — ssh.exe's argv parser strips them, so the guest
            // received an unquoted $(dirname '...') that would word-split on
            // any path with spaces. Compute it here and single-quote it.
            string guestDir = EscapeSingleQuotes(GuestParentDirectory(guestPath));

            const int maxCopyAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                string tmpRemote = $"/tmp/vmcreate-copy-{Guid.NewGuid():N}.b64";
                try
                {
                    int offset = 0;
                    bool first = true;
                    while (offset < base64.Length)
                    {
                        int length = Math.Min(CopyChunkBase64Length, base64.Length - offset);
                        string piece = base64.Substring(offset, length);
                        string redirect = first ? ">" : ">>";
                        string command = $"printf '%s' '{piece}' {redirect} '{tmpRemote}'";
                        // No per-chunk retry (see remarks): the whole copy
                        // restarts below on a transport error.
                        await RunCommandInternalAsync(command, CommandTimeout, ct);
                        offset += length;
                        first = false;
                    }

                    // Empty payload: create an empty temp file so the decode yields
                    // an empty target instead of failing on a missing file.
                    if (first)
                        await RunCommandInternalAsync($": > '{tmpRemote}'", CommandTimeout, ct);

                    string decode = $@"
                        sudo mkdir -p '{guestDir}'
                        base64 -d '{tmpRemote}' | sudo tee '{safePath}' > /dev/null
                        sudo chmod {chmodMode} '{safePath}'
                        sudo chown root:root '{safePath}'
                    ";
                    await RunCommandInternalAsync(decode, CommandTimeout, ct);
                    return;
                }
                catch (Exception ex) when (attempt < maxCopyAttempts && IsSshTransportError(ex))
                {
                    _logger.LogWarning("SSH transport error during copy to {GuestPath} (attempt {Attempt}/{Max}); restarting the transfer from scratch: {Message}",
                        guestPath, attempt, maxCopyAttempts, ex.Message);
                    _vmIpAddress = null;
                    await Task.Delay(RetryDelay, ct);
                    _vmIpAddress = await DiscoverVmIpAsync(ct);
                    try { await RunCommandInternalAsync($"rm -f '{tmpRemote}'", CommandTimeout, ct); }
                    catch { /* best effort — the GUID-named temp leaks harmlessly */ }
                }
                finally
                {
                    // Best-effort temp cleanup; failures are harmless in /tmp.
                    try { await RunCommandInternalAsync($"rm -f '{tmpRemote}'", CommandTimeout, ct); }
                    catch { _logger.LogDebug("Temp copy file {Path} left behind on VM {VMName}", tmpRemote, VmName); }
                }
            }
        }

        /// <summary>
        /// POSIX dirname for guest paths, computed host-side (no $(dirname)
        /// substitution on the guest: quoting through ssh argv parsing is
        /// fragile and space-containing paths word-split).
        /// </summary>
        private static string GuestParentDirectory(string guestPath)
        {
            int idx = guestPath.LastIndexOf('/');
            if (idx < 0) return ".";
            if (idx == 0) return "/";
            return guestPath.Substring(0, idx);
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

            if (args.Length > MaxCommandLength)
            {
                throw new InvalidOperationException(
                    $"SSH command for VM '{VmName}' is {args.Length} characters (limit {MaxCommandLength}). " +
                    "Windows cannot pass a command line this long to ssh.exe. " +
                    "Transfer the payload as a file via CopyContentAsync/CopyFileAsync (chunked) and execute it on the guest instead.");
            }

            // NOTE: never log the full ssh argument list. The remote command
            // can embed guest payload (CopyContentAsync chunks are base64
            // script/secret material on the command line), and the plaintext
            // rolling log lives in %TEMP%. Log the transport options only.
            _logger.LogDebug("SSH exec on VM {VMName} ({Length} chars)", VmName, linuxCommand.Length);

            var psi = new ProcessStartInfo
            {
                // Absolute path: a bare "ssh" resolves via PATH (hijackable;
                // also absent from service contexts). Windows ships OpenSSH
                // exactly here.
                FileName = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\OpenSSH\ssh.exe"),
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
