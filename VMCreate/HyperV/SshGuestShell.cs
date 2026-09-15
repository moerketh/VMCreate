using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
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
        // Discovered lazily by WaitForReadyAsync; null until the VM's IP is known.
        private string? _vmIpAddress;

        // Host-key TOFU pinning: per-VM known_hosts path (see
        // ResetHostKeyPinning). Written by ssh itself on first connect
        // (accept-new); every subsequent exec in this deployment then
        // verifies against the recorded key and hard-fails on a mismatch.
        private string? _hostKeyKnownHostsPath;

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

        /// <summary>
        /// Arms per-deployment host-key pinning. Call before the first SSH
        /// connection: deletes any stale known_hosts left by a previous
        /// deployment of the same VM name and provisions a fresh file so
        /// the guest's *current* host key is the one that gets trusted on
        /// first use. Without this, a re-created VM whose image regenerated
        /// its SSH host keys would hard-fail against the old pinned entry,
        /// and a stale file from an aborted deploy could pin the wrong VM.
        /// </summary>
        public void ResetHostKeyPinning()
        {
            string directory = Path.Combine(Path.GetTempPath(), "VMCreate", "known_hosts");
            string path = Path.Combine(directory, $"{SanitizeVmNameForFileName(VmName)}.known_hosts");

            Directory.CreateDirectory(directory);
            if (File.Exists(path))
                File.Delete(path);
            // ssh appends the entry itself on first connect; we just need the
            // (empty) file to exist so it isn't created with inherited ACLs
            // from a different parent (and so `accept-new` never falls back
            // to the user's global file).
            using (File.Create(path)) { }
            _hostKeyKnownHostsPath = path;
        }

        /// <summary>
        /// VmName becomes part of the known_hosts file name; strip anything
        /// Windows forbids in file names plus path separators.
        /// </summary>
        private static string SanitizeVmNameForFileName(string vmName) =>
            string.Concat(vmName.Select(c => invalidFileNameChars.Contains(c) ? '_' : c));

        private static readonly char[] invalidFileNameChars = Path.GetInvalidFileNameChars();

        // ── Connection lifecycle ─────────────────────────────────────────

        /// <summary>
        /// Waits until native SSH can successfully connect to the VM.
        /// Discovers the VM's IP via Get-VMNetworkAdapter, then tests SSH connectivity.
        /// Retries every 5 seconds until the guest's SSH server is ready.
        /// </summary>
        public async Task WaitForReadyAsync(CancellationToken ct)
        {
            _logger.LogInformation("Waiting for SSH to become available on VM {VMName}...", VmName);

            // Per-deployment TOFU pinning arm: fresh known_hosts before the
            // very first connection attempt (see ResetHostKeyPinning).
            ResetHostKeyPinning();

            var deadline = DateTime.UtcNow + ReadyTimeout;
            Exception? lastError = null;

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
                    string? result = await RunCommandInternalAsync("echo 'ssh-ready'", TimeSpan.FromSeconds(15), ct);
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
            string safePath = SshTransport.EscapeSingleQuotes(guestPath);
            // Host-side dirname: the previous $@"sudo mkdir -p ""$(dirname
            // '{safePath}')""..." embedded real double quotes in a verbatim
            // string — ssh.exe's argv parser strips them, so the guest
            // received an unquoted $(dirname '...') that would word-split on
            // any path with spaces. Compute it here and single-quote it.
            string guestDir = SshTransport.EscapeSingleQuotes(GuestParentDirectory(guestPath));

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
                }
                finally
                {
                    // Best-effort temp cleanup; failures are harmless in /tmp.
                    // Runs on every exit path (success, guest error, transport
                    // retry), so the transport-retry catch above does NOT need
                    // its own rm -f — a second one there was pure duplication.
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

        /// <summary>Delegates to the shared transport (adapter ordering matters:
        /// post-boot SSH rides the temporary NIC, so 'VMCreate Temp' wins).</summary>
        private async Task<string?> DiscoverVmIpAsync(CancellationToken ct)
            => await SshTransport.DiscoverVmIpAsync(VmName, ct, preferVmCreateTempAdapter: true);

        private async Task<string> RunWithRetryAsync(string linuxCommand, TimeSpan timeout, CancellationToken ct)
        {
            Exception? lastEx = null;
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

            // NOTE: never log the full ssh argument list. The remote command
            // can embed guest payload (CopyContentAsync chunks are base64
            // script/secret material on the command line), and the plaintext
            // rolling log lives in %TEMP%. Log the transport options only.
            _logger.LogDebug("SSH exec on VM {VMName} ({Length} chars)", VmName, linuxCommand.Length);

            // Host-key TOFU pinning: the per-VM known_hosts file is created
            // fresh for every deployment in WaitForReadyAsync — see
            // ResetHostKeyPinning. All execs in this shell instance then
            // trust-and-record the guest's host key and hard-fail if a
            // later exec sees a different key (e.g. another VM took over
            // the IP address mid-deployment).
            return await SshTransport.ExecuteAsync(
                _logger, VmName, _privateKeyPath, _vmIpAddress!, AutomationUser,
                _hostKeyKnownHostsPath, linuxCommand, timeout, ct, maxArgumentLength: MaxCommandLength);
        }
    }
}
