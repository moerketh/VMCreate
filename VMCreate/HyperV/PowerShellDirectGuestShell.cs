using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Management.Automation;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV;

namespace VMCreate
{
    /// <summary>
    /// Implements <see cref="IGuestShell"/> using PowerShell Direct (Invoke-Command over VMBus).
    /// PowerShell Direct works over the Hyper-V VMBus connection and does not require
    /// network connectivity or SSH — it only needs the VM name and credentials.
    ///
    /// This implementation is used for Windows VMs (e.g. FLARE VM) where SSH is not
    /// available and PowerShell Direct is the native remote management transport.
    /// </summary>
    public class PowerShellDirectGuestShell : IGuestShell
    {
        private readonly ILogger _logger;
        private readonly string _vmName;
        private readonly string _username;
        private readonly PSCredential _credential;

        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(600);
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

        public string VmName => _vmName;

        public PowerShellDirectGuestShell(ILogger logger, string vmName, string username, string password)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _vmName = vmName ?? throw new ArgumentNullException(nameof(vmName));
            _username = username ?? throw new ArgumentNullException(nameof(username));

            // The plaintext password is ONLY used to build the SecureString
            // credential. It is deliberately NOT retained in a field — a
            // lingering _password beside the SecureString defeats the point
            // of the SecureString (dumpable via reflection/heap inspection).
            var securePassword = new SecureString();
            foreach (char c in password)
                securePassword.AppendChar(c);
            securePassword.MakeReadOnly();
            _credential = new PSCredential(username, securePassword);
        }

        // ── Connection lifecycle ─────────────────────────────────────────

        /// <summary>
        /// Waits until PowerShell Direct can successfully connect to the VM.
        /// Polls every 5 seconds until the VM is ready or the timeout is reached.
        /// Skips the initial 60-second sleep if the VM is already responsive.
        /// </summary>
        public async Task WaitForReadyAsync(CancellationToken ct)
        {
            _logger.LogInformation("Waiting for PowerShell Direct to become available on VM {VMName}...", _vmName);

            // Try an immediate probe first — if the VM is already up and responsive
            // (e.g. after a post-boot step that didn't reboot), skip the 60-second sleep.
            try
            {
                string? probe = await RunCommandInternalAsync("Write-Output 'ps-direct-ready'", TimeSpan.FromSeconds(10), ct);
                if (probe != null && probe.Contains("ps-direct-ready"))
                {
                    _logger.LogInformation("PowerShell Direct is ready on VM {VMName} (no wait needed)", _vmName);
                    return;
                }
            }
            catch (Exception ex) when (ex.Message?.Contains("remote session might have ended") == true
                                    || ex.Message?.Contains("cannot handle") == true
                                    || ex.Message?.Contains("not yet available") == true)
            {
                _logger.LogDebug("VM {VMName} not yet ready, beginning initial wait...", _vmName);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation must propagate — the old bare catch { } swallowed
                // it (making shutdown/cancel hangs look like a VM readiness
                // problem) and then slept 60s anyway.
                throw;
            }
            catch
            {
                _logger.LogDebug("VM {VMName} probe failed, beginning initial wait...", _vmName);
            }

            // Give the VM time to complete OOBE before starting to poll.
            // OOBE on a large VHDX can take several minutes, including reboots.
            _logger.LogInformation("Waiting 60 seconds for VM {VMName} to complete initial boot...", _vmName);
            await Task.Delay(TimeSpan.FromSeconds(60), ct);

            var deadline = DateTime.UtcNow + ReadyTimeout;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    string? result = await RunCommandInternalAsync("Write-Output 'ps-direct-ready'", TimeSpan.FromSeconds(15), ct);
                    if (result != null && result.Contains("ps-direct-ready"))
                    {
                        _logger.LogInformation("PowerShell Direct is ready on VM {VMName}", _vmName);
                        return;
                    }
                }
                catch (Exception ex) when (ex.Message?.Contains("remote session might have ended") == true
                                        || ex.Message?.Contains("cannot handle") == true)
                {
                    // Transient failures during OOBE/reboot are expected — log concisely
                    _logger.LogDebug("PowerShell Direct not ready yet on VM {VMName}, retrying...", _vmName);
                }
                catch (Exception ex)
                {
                    // Unexpected errors — log with full exception details
                    _logger.LogDebug(ex, "PowerShell Direct not yet available on VM {VMName}, retrying...", _vmName);
                }

                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }

            throw new TimeoutException($"PowerShell Direct did not become available on VM {_vmName} within {ReadyTimeout.TotalSeconds}s");
        }

        // ── Command execution ────────────────────────────────────────────

        /// <summary>
        /// Executes a PowerShell command on the guest VM via PowerShell Direct and returns stdout.
        /// </summary>
        public async Task<string> RunCommandAsync(string command, CancellationToken ct)
        {
            // SECURITY: never log the command body. CopyContentAsync/
            // CopyFileAsync embed base64 payload (configs, scripts, secrets)
            // on the command line — a 200-char preview still leaks ~145
            // characters of it into the plaintext %TEMP% log. Mirrors the
            // SSH transport (SshGuestShell.RunCommandInternalAsync): log
            // target and length only.
            _logger.LogDebug("Running PowerShell Direct command on VM {VMName} ({Length} chars)", _vmName, command?.Length ?? 0);
            string? result = await RunCommandInternalAsync(command, CommandTimeout, ct);
            _logger.LogDebug("PowerShell Direct command completed on VM {VMName} ({Length} chars)", _vmName, result?.Length ?? 0);
            return result!;
        }

        /// <summary>
        /// Executes a PowerShell command with an explicit timeout. PowerShell Direct has no
        /// command-line length cap, so the timeout is the only relevant bound.
        /// </summary>
        public async Task<string> RunCommandAsync(string command, TimeSpan timeout, CancellationToken ct)
        {
            // SECURITY: length only — see RunCommandAsync above.
            _logger.LogDebug("Running PowerShell Direct command on VM {VMName} ({Length} chars)", _vmName, command?.Length ?? 0);
            string? result = await RunCommandInternalAsync(command, timeout, ct);
            _logger.LogDebug("PowerShell Direct command completed on VM {VMName} ({Length} chars)", _vmName, result?.Length ?? 0);
            return result!;
        }

        /// <summary>
        /// Writes string content to a file on the guest VM via PowerShell Direct.
        /// </summary>
        public async Task CopyContentAsync(string content, string guestPath, CancellationToken ct)
        {
            _logger.LogInformation("Writing content to {Path} on VM {VMName} via PowerShell Direct", guestPath, _vmName);

            // Base64-encode the content to avoid escaping issues
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            string script = $@"
                $bytes = [Convert]::FromBase64String('{base64}')
                $dir = Split-Path -Parent '{EscapeForPowerShell(guestPath)}'
                if (-not (Test-Path $dir)) {{ New-Item -ItemType Directory -Path $dir -Force | Out-Null }}
                [System.IO.File]::WriteAllBytes('{EscapeForPowerShell(guestPath)}', $bytes)
            ";

            await RunCommandInternalAsync(script, CommandTimeout, ct);
        }

        /// <summary>
        /// Writes SECRET string content (keys, credentials) to the guest via
        /// PowerShell Direct (Windows guests). Honours the IGuestShell
        /// contract: the file exists only in its final permission state —
        /// it is created empty, the SYSTEM/Administrators-only ACL is
        /// applied and VERIFIED (icacls exit code checked, not assumed)
        /// before any secret bytes are written, so there is never a
        /// world-readable intermediate. A failed ACL application throws
        /// instead of returning success: the guarantee must be loud.
        /// The command is executed on an internal path that never logs the
        /// command body, so key material cannot reach the plaintext %TEMP%
        /// log regardless of the configured level.
        /// </summary>
        public async Task CopySecretAsync(string content, string guestPath, CancellationToken ct)
        {
            _logger.LogInformation("Writing secret content to {Path} on VM {VMName} via PowerShell Direct", guestPath, _vmName);

            // Base64-encode the content to avoid escaping issues
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            string script = $@"
                $bytes = [Convert]::FromBase64String('{base64}')
                $dir = Split-Path -Parent '{EscapeForPowerShell(guestPath)}'
                if (-not (Test-Path $dir)) {{ New-Item -ItemType Directory -Path $dir -Force | Out-Null }}
                # Create the target EMPTY first so the secret never exists in
                # an unrestricted state: restrict+verify the empty placeholder
                # BEFORE the bytes touch disk. Order matters — if the write came
                # first, the file would carry inherited ACEs (BUILTIN\Users read
                # from the parent directory) until icacls ran.
                New-Item -ItemType File -Path '{EscapeForPowerShell(guestPath)}' -Force | Out-Null
                # Strip inherited ACEs and grant SYSTEM + Administrators only —
                # the Windows equivalent of the SSH path's root:root 0600.
                # Well-known SIDs instead of group names: 'SYSTEM'/'Administrators'
                # are localized on non-English guests (e.g. 'Administratoren').
                # No (OI)(CI) inheritance flags: the target is a file.
                icacls '{EscapeForPowerShell(guestPath)}' /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
                # icacls reports failures on STDOUT with a quiet non-zero
                # $LASTEXITCODE (native stderr stays empty, so HadErrors at the
                # host sees nothing). The only reliable failure signal is the
                # exit code — and a silent failure here would leave the file
                # world-readable while the method returns success and logs
                # '(SYSTEM/Administrators only)', a silently-broken guarantee.
                # Loud failure instead: throw into the PS error stream.
                if ($LASTEXITCODE -ne 0) {{ throw ""icacls ($LASTEXITCODE) failed setting the file ACL on '{EscapeForPowerShell(guestPath)}'"" }}
                # ACL verified — safe to write the secret bytes into the
                # already-restricted file.
                [System.IO.File]::WriteAllBytes('{EscapeForPowerShell(guestPath)}', $bytes)
            ";

            await RunCommandInternalAsync(script, CommandTimeout, ct);
            _logger.LogInformation("Wrote secret -> {Path} on VM {VMName} (SYSTEM/Administrators only)", guestPath, _vmName);
        }

        /// <summary>
        /// Copies a host file to the guest VM via PowerShell Direct.
        /// Uses base64 encoding to transfer the file content through the PowerShell Direct channel.
        /// </summary>
        public async Task CopyFileAsync(string hostPath, string guestPath, CancellationToken ct)
        {
            _logger.LogInformation("Copying file {HostPath} to {GuestPath} on VM {VMName} via PowerShell Direct", hostPath, guestPath, _vmName);

            byte[] fileBytes = await Task.Run(() => System.IO.File.ReadAllBytes(hostPath), ct);
            string base64 = Convert.ToBase64String(fileBytes);

            var sb = new StringBuilder();
            sb.AppendLine($"$dir = Split-Path -Parent '{EscapeForPowerShell(guestPath)}'");
            sb.AppendLine("if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }");
            sb.AppendLine($"$allBytes = [Convert]::FromBase64String('{base64}')");
            sb.AppendLine($"[System.IO.File]::WriteAllBytes('{EscapeForPowerShell(guestPath)}', $allBytes)");

            await RunCommandInternalAsync(sb.ToString(), CommandTimeout, ct);
        }

        // ── Internal implementation ──────────────────────────────────────

        private async Task<string?> RunCommandInternalAsync(string? script, TimeSpan timeout, CancellationToken ct)
        {
            // Fresh CreateDefault2 + Hyper-V runspace per call (same shape
            // as the executor's CreateRunspace). See HostPowerShell for why
            // host-side hosting must not use the default InitialSessionState.
            using var runspace = HostPowerShell.CreateRunspace();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            // Use Invoke-Command to run the script inside the VM via PowerShell Direct.
            // Invoke-Command is a Core cmdlet; the Hyper-V module (needed for the
            // -VMName remoting transport) is pre-imported through the runspace ISS.
            ps.AddCommand("Invoke-Command")
                .AddParameter("VMName", _vmName)
                .AddParameter("Credential", _credential)
                .AddParameter("ScriptBlock", ScriptBlock.Create(script));

            // Enforce the per-attempt timeout so a single blocking call (e.g. during a
            // guest reboot) cannot stall the poll loop indefinitely.
            // Task.Run with a CancellationToken only checks the token before the delegate
            // starts — it cannot interrupt ps.Invoke() once it's running. Instead, race
            // the invoke against a delay and call ps.Stop() to actually abort the pipeline.
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var invokeTask = Task.Run(() => ps.Invoke());
            if (await Task.WhenAny(invokeTask, Task.Delay(timeout, delayCts.Token)) != invokeTask)
            {
                // The timeout (or overall cancellation) won before Invoke returned — abort the
                // pipeline and observe the aborted task so it isn't an unobserved exception.
                ps.Stop();
                _ = invokeTask.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"PowerShell Direct command timed out on VM {_vmName} after {timeout.TotalSeconds}s");
            }

            // Invoke finished first — cancel the pending timeout delay and observe the result.
            delayCts.Cancel();
            System.Collections.ObjectModel.Collection<PSObject> result = await invokeTask;

            if (ps.HadErrors)
            {
                string errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                throw new Exception($"PowerShell Direct errors on VM {_vmName}: {errors}");
            }

            var output = new StringBuilder();
            foreach (var item in result)
            {
                if (item?.BaseObject != null)
                    output.AppendLine(item.BaseObject.ToString());
            }

            // Also capture host/information-stream content (e.g. Write-Host output from
            // customization scripts). It does not appear in the pipeline output, so without
            // this the strings callers log would be empty even on success.
            foreach (var info in ps.Streams.Information)
            {
                if (info?.MessageData != null)
                    output.AppendLine(info.MessageData.ToString());
            }

            return output.ToString();
        }

        private static string EscapeForPowerShell(string s)
        {
            return s.Replace("'", "''");
        }
    }
}