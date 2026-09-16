using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Shared ssh.exe transport used by both host-side SSH call sites:
    /// <see cref="SshGuestShell"/> (post-boot automation via the generated
    /// per-user key) and <c>GuestDiagnosticsCollector</c> (ISO-cycle failure
    /// forensics). Previously each class shipped its own copy of the
    /// argument-building, process, timeout and stderr-filtering logic — a
    /// drift hazard for anything security-relevant (host-key checking,
    /// secret-bearing command lines, error redaction). One transport, one
    /// place to pin host keys.
    /// </summary>
    public static class SshTransport
    {
        /// <summary>ssh.exe ships at this absolute path on Windows.</summary>
        public static readonly string SshExePath =
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\OpenSSH\ssh.exe");

        /// <summary>
        /// Builds the ssh.exe argument string. Kept internal and pure so tests
        /// can assert the exact transport posture (host-key checking, batch
        /// mode, agent/known-hosts, user@ip placement, command escaping).
        /// </summary>
        /// <param name="privateKeyPath">Private identity for key-based auth.</param>
        /// <param name="vmIpAddress">Guest IPv4 as discovered from Hyper-V.</param>
        /// <param name="username">Guest user (vmcreate for automation; ubuntu on the ISO cycle guest).</param>
        /// <param name="knownHostsFile">
        /// Optional per-VM known_hosts path. When provided, host keys are
        /// pinned with TOFU semantics: <c>StrictHostKeyChecking=accept-new</c>
        /// trusts the first host key the VM presents and records it in the
        /// caller-managed file; any subsequent presentation of a *different*
        /// key for the same entry fails the connection. When null (the
        /// diagnostics path), checking stays disabled and entries go to NUL —
        /// the ISO-cycle guest is a transient boot identity whose key never
        /// matches the deployed disk anyway.
        /// </param>
        /// <param name="linuxCommand">Bash command for the guest.</param>
        internal static string BuildArguments(
            string privateKeyPath, string vmIpAddress, string username,
            string? knownHostsFile, string linuxCommand)
        {
            // Normalize Windows CRLF → LF so bash doesn't choke on \r
            linuxCommand = linuxCommand.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            var args = new StringBuilder();
            args.Append($"-i \"{privateKeyPath}\" ");
            if (!string.IsNullOrEmpty(knownHostsFile))
            {
                // TOFU pinning: trust first use, record to the per-VM file,
                // hard-fail on any later key change for the same host entry.
                args.Append("-o StrictHostKeyChecking=accept-new ");
                args.Append($"-o UserKnownHostsFile=\"{knownHostsFile}\" ");
            }
            else
            {
                // No pinning (ISO-cycle diagnostics guest): freshly-created
                // local guests, keys never persist to a host file.
                args.Append("-o StrictHostKeyChecking=no ");
                args.Append("-o UserKnownHostsFile=NUL ");
            }
            args.Append("-o BatchMode=yes ");
            args.Append("-o ConnectTimeout=10 ");
            args.Append($"{username}@{vmIpAddress} ");
            args.Append($"bash -c {EscapeForSsh(linuxCommand)}");
            return args.ToString();
        }

        /// <summary>
        /// Executes one ssh.exe invocation. Shared process semantics:
        /// async stdout/stderr capture, linked-CancellationToken timeout with
        /// full process-tree kill, exit-code failure with the benign
        /// "Permanently added" notice filtered out of the error detail.
        /// Never logs the ssh arguments — the remote command can embed
        /// guest payload (chunked base64 script/secret material), and the
        /// plaintext rolling log lives in %TEMP%. Log target and length only.
        /// </summary>
        /// <param name="maxArgumentLength">
        /// Optional argv cap. Windows CreateProcess command lines are limited
        /// to 32,767 chars; the shell enforces a lower budget so oversized
        /// payloads fail with a actionable message instead of
        /// Win32Exception 206.
        /// </param>
        /// <param name="tolerateNonZeroExit">
        /// Diagnostics-only tolerance: when true and the ssh process exits
        /// nonzero, whatever stdout was captured before the failure is
        /// returned (partial forensics beat a clean failure on a guest that
        /// is already in a bad state); an exception is still thrown when no
        /// stdout was captured at all. When false (automation path), any
        /// nonzero exit throws.
        /// </param>
        /// <remarks>
        /// INVARIANT: on failure, <see cref="FilterSignificantStderr"/> output
        /// (stderr, or stdout as fallback) flows into the thrown exception
        /// message — from there into VmDeploymentResult.ErrorMessage (shown
        /// in the GUI) and, via orchestration logging at Warning/Error, into
        /// the PLAINTEXT log in %TEMP%. Guest stderr/stdout is therefore an
        /// unconditional channel into that log. No current step echoes
        /// credentials to stderr, and base64 payloads are quote-safe so
        /// quoting errors cannot make bash echo a copied chunk — but any
        /// future step MUST NOT print secrets (credentials, key material)
        /// to guest stdout/stderr: they would land in the plaintext log
        /// regardless of the configured level. Log classifications or
        /// lengths instead (see HtbApiClient.ClassifyContent for the
        /// pattern).
        /// </remarks>
        public static async Task<string> ExecuteAsync(
            ILogger logger,
            string vmName,
            string privateKeyPath,
            string vmIpAddress,
            string username,
            string? knownHostsFile,
            string linuxCommand,
            TimeSpan timeout,
            CancellationToken ct,
            int? maxArgumentLength = null,
            bool tolerateNonZeroExit = false)
        {
            string args = BuildArguments(privateKeyPath, vmIpAddress, username, knownHostsFile, linuxCommand);

            if (maxArgumentLength.HasValue && args.Length > maxArgumentLength.Value)
            {
                throw new InvalidOperationException(
                    $"SSH command line for VM '{vmName}' is {args.Length} characters (limit {maxArgumentLength.Value}). " +
                    "Windows cannot pass a command line this long to ssh.exe. " +
                    "Transfer the payload as a file via CopyContentAsync/CopyFileAsync (chunked) and execute it on the guest instead.");
            }

            var psi = new ProcessStartInfo
            {
                // Absolute path: a bare "ssh" resolves via PATH (hijackable;
                // also absent from service contexts). Windows ships OpenSSH
                // exactly here.
                FileName = SshExePath,
                Arguments = args,
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
                    $"SSH command timed out after {timeout.TotalSeconds}s on VM '{vmName}'");
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
                string errorDetail = !string.IsNullOrEmpty(FilterSignificantStderr(stderrStr)) ? stderrStr.Trim() : stdoutStr.Trim();

                if (tolerateNonZeroExit && !string.IsNullOrWhiteSpace(stdoutStr))
                {
                    logger.LogWarning("SSH command exited with code {ExitCode}; returning partial stdout: {Error}",
                        process.ExitCode, errorDetail);
                    return stdoutStr;
                }

                throw new Exception(
                    $"SSH command failed (exit code {process.ExitCode}) on VM '{vmName}': {errorDetail}");
            }

            if (!string.IsNullOrWhiteSpace(stderrStr))
            {
                var significantLines = stderrStr
                    .Split('\n')
                    .Where(line => !string.IsNullOrWhiteSpace(line)
                                && !line.Contains("Permanently added", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (significantLines.Length > 0)
                    logger.LogDebug("SSH stderr (non-fatal): {Stderr}", string.Join("\n", significantLines).Trim());
            }

            return stdoutStr;
        }

        /// <summary>
        /// Filters stderr down to significant lines (drops blanks and the
        /// expected "Permanently added" known-hosts notice).
        /// </summary>
        public static string FilterSignificantStderr(string? stderrStr)
        {
            var significantLines = (stderrStr ?? string.Empty)
                .Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line)
                            && !line.Contains("Permanently added", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return string.Join("\n", significantLines).Trim();
        }

        /// <summary>
        /// Discovers the guest's IPv4 via Get-VMNetworkAdapter. Shared by the
        /// shell and the diagnostics collector so adapter selection can't
        /// drift; preferVmCreateTempAdapter mirrors SshGuestShell's sort (the
        /// temporary adapter added for post-boot SSH wins over any other).
        /// Adapter ordering and IPv4 selection run in C# on the host: the
        /// host-side script is a bare Get-VMNetworkAdapter call from the
        /// Hyper-V module pre-imported in the shared session state — no
        /// Utility cmdlets (New-Object, Sort-Object) and no foreach loops,
        /// so it stays viable under a CreateDefault2 runspace and the
        /// trimmed hosting package.
        /// </summary>
        public static async Task<string?> DiscoverVmIpAsync(string vmName, CancellationToken ct, bool preferVmCreateTempAdapter)
        {
            // Fresh CreateDefault2 + Hyper-V runspace per call (static method
            // has no shared ISS — same per-call shape the executor uses per
            // RunCommandAsync). See HostPowerShell for why host-side hosting
            // must not use the default InitialSessionState.
            using var runspace = VMCreate.HyperV.HostPowerShell.CreateRunspace();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            // -ErrorAction SilentlyContinue: a missing VM surfaces as an
            // empty result instead of a terminating error.
            ps.AddScript($"Get-VMNetworkAdapter -VMName '{vmName.Replace("'", "''")}' -ErrorAction SilentlyContinue");

            var result = await Task.Run(() => ps.Invoke(), ct);

            // 'VMCreate Temp' first when preferred, preserving
            // Get-VMNetworkAdapter's own order otherwise; then the first
            // adapter reporting any IPv4 wins.
            var adapters = preferVmCreateTempAdapter
                ? result
                    .Where(a => string.Equals(GetNetworkAdapterName(a), "VMCreate Temp", StringComparison.OrdinalIgnoreCase))
                    .Concat(result.Where(a => !string.Equals(GetNetworkAdapterName(a), "VMCreate Temp", StringComparison.OrdinalIgnoreCase)))
                : result;

            foreach (var adapter in adapters)
            {
                foreach (string? ip in GetAdapterIpAddresses(adapter))
                {
                    if (!string.IsNullOrEmpty(ip) && IPv4Regex.IsMatch(ip))
                        return ip;
                }
            }

            return null;
        }

        private static string GetNetworkAdapterName(PSObject adapter)
            => adapter.Properties["Name"]?.Value?.ToString() ?? string.Empty;

        private static System.Collections.Generic.IEnumerable<string> GetAdapterIpAddresses(PSObject adapter)
        {
            if (adapter.Properties["IPAddresses"]?.Value is not System.Collections.IEnumerable raw
                || raw is string)
            {
                yield break;
            }

            foreach (object? ip in raw)
            {
                yield return ip?.ToString() ?? string.Empty;
            }
        }

        private static readonly System.Text.RegularExpressions.Regex IPv4Regex =
            new("^\\d+\\.\\d+\\.\\d+\\.\\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Escapes a value for safe embedding inside a single-quoted bash string.
        /// Closes the quote, inserts an escaped literal quote, and re-opens the quote.
        /// </summary>
        public static string EscapeSingleQuotes(string value) =>
            value.Replace("'", "'\\''");

        /// <summary>
        /// Escapes a full bash command for embedding in ssh.exe's argv (the
        /// argv parser strips the outer quotes; the guest must receive the
        /// entire command as one single-quoted bash -c string with internal
        /// quotes re-opened the POSIX way).
        /// </summary>
        public static string EscapeForSsh(string command)
        {
            string escaped = command.Replace("'", "'\\''");
            return $"'{escaped}'";
        }
    }
}