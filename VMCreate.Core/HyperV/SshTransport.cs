using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
            => BuildArguments(privateKeyPath, SshConnectionTarget.ForIp(vmIpAddress), username, knownHostsFile, linuxCommand);

        /// <summary>Target-aware overload: vsock targets carry their
        /// ProxyCommand in the target's extra arguments.</summary>
        internal static string BuildArguments(
            string privateKeyPath, SshConnectionTarget target, string username,
            string? knownHostsFile, string linuxCommand)
        {
            // Normalize Windows CRLF → LF so bash doesn't choke on \r
            linuxCommand = linuxCommand.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            var args = new StringBuilder();
            args.Append($"-i \"{privateKeyPath}\" ");
            if (target.ExtraArguments is not null)
                args.Append(target.ExtraArguments);
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
            // ServerAlive keepalives: a guest whose IP changed mid-pipeline (e.g.
            // a NetworkManager restart re-DHCPing the interface) black-holes the
            // TCP session — packets go into the void with no RST, and ssh.exe
            // would otherwise sit until the host-side timeout. Three missed
            // 5s probes terminate the client in ~15s so the transport-error
            // retry path (nulls the IP, re-discovers, reconnects) can run.
            args.Append("-o ServerAliveInterval=5 ");
            args.Append("-o ServerAliveCountMax=3 ");
            args.Append($"{username}@{target.Host} ");
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
            bool tolerateNonZeroExit = false,
            string? stdinContent = null)
            => await ExecuteAsync(
                logger, vmName, privateKeyPath, SshConnectionTarget.ForIp(vmIpAddress), username,
                knownHostsFile, linuxCommand, timeout, ct,
                maxArgumentLength, tolerateNonZeroExit, stdinContent);

        /// <summary>
        /// Target-aware execution with optional stdout line streaming and an
        /// idle timeout. Streaming exists for long guest work (partclone):
        /// the buffered return-at-exit semantics of the classic overload
        /// cannot report progress while the command runs, and a single total
        /// timeout cannot distinguish "healthy but slow" from "hung". The
        /// idle budget bounds SILENCE: any stdout line resets it, so a
        /// command that keeps talking stays alive as long as the total
        /// timeout allows, while a mute one fails at the idle deadline.
        /// </summary>
        /// <param name="onStdoutLine">Invoked per stdout line as it arrives
        /// (before buffering). Must be cheap; exceptions propagate and kill
        /// the process tree.</param>
        /// <param name="idleTimeout">When non-null, the maximum silence
        /// between stdout lines. Distinct from <paramref name="timeout"/>,
        /// which still bounds the whole run.</param>
        public static async Task<string> ExecuteAsync(
            ILogger logger,
            string vmName,
            string privateKeyPath,
            SshConnectionTarget target,
            string username,
            string? knownHostsFile,
            string linuxCommand,
            TimeSpan timeout,
            CancellationToken ct,
            int? maxArgumentLength = null,
            bool tolerateNonZeroExit = false,
            string? stdinContent = null,
            Action<string>? onStdoutLine = null,
            TimeSpan? idleTimeout = null)
        {
            string args = BuildArguments(privateKeyPath, target, username, knownHostsFile, linuxCommand);

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
                RedirectStandardInput = !string.IsNullOrEmpty(stdinContent),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            // Idle clock: every stdout line stamps lastStdoutAt; the wait
            // loop below enforces the budget. Captured local written from
            // the OutputDataReceived thread-pool thread and polled by the
            // wait loop's continuation — benign staleness on the read side
            // only delays idle detection by one 250 ms tick.
            var lastStdoutAt = DateTime.UtcNow;
            // A throwing stdout callback must never take down the process:
            // OutputDataReceived runs on a thread-pool thread where an
            // unhandled exception kills VMCreate outright. Capture it, kill
            // the ssh process tree, and rethrow from the wait below so the
            // caller sees its own exception with the transport cleaned up.
            Exception? callbackException = null;

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stdout) stdout.AppendLine(e.Data);
                lastStdoutAt = DateTime.UtcNow;
                if (onStdoutLine != null)
                {
                    try { onStdoutLine(e.Data); }
                    catch (Exception cbEx)
                    {
                        callbackException = cbEx;
                        try { process.Kill(entireProcessTree: true); } catch { }
                    }
                }
            };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!string.IsNullOrEmpty(stdinContent))
            {
                // Secret channel: material such as the sudo password for
                // `sudo -S` travels ONLY here, on ssh's stdin — never on the
                // command line (ssh argv, guest bash -c string, guest ps
                // output) and never in logs. Write + close; ssh closes the
                // remote end after the bytes flush.
                await process.StandardInput.WriteAsync(stdinContent);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                if (idleTimeout.HasValue)
                {
                    // Delegate form: the process kill lives HERE (the helper
                    // is transport-agnostic for tests), the timing lives there.
                    try
                    {
                        await IdleAwareWaitAsync(
                            () => process.HasExited,
                            token => process.WaitForExitAsync(token),
                            () => lastStdoutAt,
                            idleTimeout.Value,
                            timeoutCts.Token,
                            vmName);
                    }
                    catch (TimeoutException)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        throw;
                    }
                }
                else
                    await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (TimeoutException) when (callbackException is not null)
            {
                // The callback's kill raced the idle deadline: the callback
                // is the real cause; report it, not the timeout.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackException).Throw(); throw;
            }
            catch (OperationCanceledException) when (callbackException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackException).Throw(); throw;
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

            // Normal exit with a captured callback exception (the handler's
            // kill ended the process before any timeout): rethrow it so the
            // caller sees its own failure. ExceptionDispatchInfo preserves
            // the callback's original stack trace.
            if (callbackException is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackException).Throw();

            string stdoutStr = stdout.ToString();
            string stderrStr = stderr.ToString();

            if (process.ExitCode != 0)
            {
                string errorDetail = !string.IsNullOrEmpty(FilterSignificantStderr(stderrStr))
                    ? stderrStr.Trim()
                    : stdoutStr.Trim();

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
        /// The idle-aware wait, pulled out so tests can drive it with fake
        /// delegates instead of a real ssh.exe. Semantics: the total
        /// deadline is enforced by <paramref name="totalTimeoutToken"/>
        /// (caller's CancelAfter — the wait delegate receives a grace token
        /// linked to it, so total timeout/cancel surfaces as
        /// OperationCanceledException from the delegate and propagates);
        /// the idle budget bounds SILENCE — if the last-output timestamp
        /// gets older than <paramref name="idleTimeout"/>, a
        /// <see cref="TimeoutException"/> with a diagnostic message
        /// (including the VM name) is thrown. 250 ms poll cadence: far below
        /// any useful idle window, cheap to spin.
        /// </summary>
        internal static async Task IdleAwareWaitAsync(
            Func<bool> hasExited,
            Func<CancellationToken, Task> waitForExitAsync,
            Func<DateTime> lastStdoutAt,
            TimeSpan idleTimeout,
            CancellationToken totalTimeoutToken,
            string vmName)
        {
            while (!hasExited())
            {
                var idle = DateTime.UtcNow - lastStdoutAt();
                if (idle > idleTimeout)
                {
                    throw new TimeoutException(
                        $"SSH command idle (no stdout for {idle.TotalSeconds:0.0}s > {idleTimeout.TotalSeconds:0.0}s) on VM '{vmName}'");
                }
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(totalTimeoutToken);
                grace.CancelAfter(TimeSpan.FromMilliseconds(250));
                try { await waitForExitAsync(grace.Token); }
                catch (OperationCanceledException) when (!totalTimeoutToken.IsCancellationRequested)
                { /* grace tick — loop and re-check the idle clock */ }
            }
            // Exited: drain to a completed state so ExitCode is available.
            await waitForExitAsync(CancellationToken.None);
        }

        /// <summary>
        /// Filters stderr down to significant lines (drops blanks and the
        /// expected "Permanently added" known-hosts notice).
        /// </summary>
        public static string FilterSignificantStderr(string? stderrStr)        {
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
        /// <remarks>
        /// Combines two signals, tie-broken by <see cref="PickGuestIp"/>:
        /// KVP (<c>Get-VMNetworkAdapter.IPAddresses</c>, only live while the
        /// guest runs hv_kvp_daemon — Omarchy's installer ISO does, the
        /// installed system does not, and its DHCP lease can change across
        /// the reboot) and the host neighbor table's MAC → IPv4 binding
        /// learned via ARP. A MAC-matched neighbor row wins over a
        /// disagreeing KVP address: it reflects where the guest is <em>now</em>.
        /// </remarks>
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
            var adapters = (preferVmCreateTempAdapter
                ? result
                    .Where(a => string.Equals(GetNetworkAdapterName(a), "VMCreate Temp", StringComparison.OrdinalIgnoreCase))
                    .Concat(result.Where(a => !string.Equals(GetNetworkAdapterName(a), "VMCreate Temp", StringComparison.OrdinalIgnoreCase)))
                : result).ToList();

            string? kvpIp = null;
            foreach (var adapter in adapters)
            {
                foreach (string? ip in GetAdapterIpAddresses(adapter))
                {
                    if (!string.IsNullOrEmpty(ip) && IPv4Regex.IsMatch(ip))
                    {
                        kvpIp = ip;
                        break;
                    }
                }
                if (kvpIp is not null)
                    break;
            }

            // KVP blind fallback: the guest is up and talking (DHCP/ARP), but
            // hv_kvp_daemon is not running, so Hyper-V reports no IPAddresses
            // (or a stale one — the installer ISO's lease). That is exactly
            // the freshly-installed Omarchy system — its installer never adds
            // the 'hyperv' package — so without this path WaitForReadyAsync
            // would poll forever on a dead lease. The host's own
            // neighbor table still learns the guest's MAC via the vmSwitch
            // (Dynamic on external switches, Permanent on the Default Switch),
            // so match the VM's adapter MACs against Get-NetNeighbor rows and
            // take the bound IPv4. MAC collision is a non-issue: Hyper-V
            // allocates 00-15-5D MACs per host, and a fabricated entry would
            // at worst send our SSH connect to a wrong address that TOFU
            // (per-VM known_hosts, fresh per deployment) then rejects.
            // MAC → IP rows, tie-broken against whatever KVP claimed.
            // Placeholder adapters: a NIC whose guest driver never
            // initialized reports 00-00-00-00-00-00 and the host keeps that
            // same zero MAC on its own Unreachable rows — matching it pins
            // discovery to a dead shared address, hence the exclusion here
            // and again on the row side inside PickNeighborMacMatch.
            var vmMacs = adapters
                .Select(GetAdapterMacAddress)
                .Where(IsUsableMacAddress)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (vmMacs.Count == 0)
                return kvpIp;

            using var ps2 = PowerShell.Create();
            ps2.Runspace = runspace;
            // IPv4 rows only; filtering and State normalization run in C#
            // below (the trimmed runspace ships the bare cmdlet call only,
            // same contract as the adapter query). MSFT_NetNeighbor exposes
            // State as a byte on the raw CIM instance, and this hosted
            // CreateDefault2 runspace returns the CimInstance unadapted —
            // Properties["State"].Value is a System.Byte ("6"), not the enum
            // name ("Permanent") an interactive console prints. Mapping the
            // byte here is what makes the fallback match at all.
            ps2.AddScript("Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue");

            var neighbors = await Task.Run(() => ps2.Invoke(), ct);
            var rows = neighbors
                .Where(n => n is not null)
                .Select(n => ReadNeighborRow(n))
                .ToList();

            return PickGuestIp(kvpIp, PickNeighborMacMatch(vmMacs, rows));
        }

        /// <summary>
        /// Reads one Get-NetNeighbor row into the (Ip, Mac, State) tuple
        /// <see cref="PickNeighborMacMatch"/> consumes. Extracted and internal
        /// so tests can pin the cross-runtime contract: a full console session
        /// adapts the CIM object (State reads "Permanent"), the app's hosted
        /// <c>InitialSessionState.CreateDefault2()</c> runspace does not
        /// (State is a raw <see cref="byte"/>). Either shape must produce the
        /// same enum-name string.
        /// </summary>
        internal static (string Ip, string Mac, string State) ReadNeighborRow(PSObject row)
        {
            return (
                Ip: row.Properties["IPAddress"]?.Value?.ToString() ?? string.Empty,
                Mac: row.Properties["LinkLayerAddress"]?.Value?.ToString() ?? string.Empty,
                State: NormalizeNeighborState(row.Properties["State"]?.Value));
        }

        /// <summary>
        /// Resolves the guest's IPv4 from the two signals Hyper-V gives us.
        /// The KVP lease (hv_kvp_daemon) wins while it still answers SSH —
        /// that is the installer live ISO. Once the guest has rebooted into
        /// the KVP-blind installed system, the lease IP is stale and the MAC
        /// → IP binding the host's vmSwitch learned via ARP is the truth, so
        /// a matching neighbor row overrides a disagreeing KVP address. When
        /// only one source knows an address, that source wins.
        /// </summary>
        internal static string? PickGuestIp(string? kvpIp, string? neighborIp)
        {
            if (string.IsNullOrEmpty(neighborIp))
                return string.IsNullOrEmpty(kvpIp) ? null : kvpIp;
            return neighborIp;
        }

        /// <summary>
        /// Normalizes MSFT_NetNeighbor State to its enum name no matter how
        /// the runspace surfaced it. The hosted CreateDefault2 runspace yields
        /// the raw CIM byte (RFC 4861 / NetTCPIP mapping: 0=Unreachable,
        /// 1=Incomplete, 2=Probe, 3=Delay, 4=Stale, 5=Reachable, 6=Permanent,
        /// 7=Failed); an interactive session yields the enum name string.
        /// Anything unrecognized maps to empty so the rank filter ignores it
        /// — a state we cannot classify must never pretend to be usable.
        /// </summary>
        internal static string NormalizeNeighborState(object? rawState)
        {
            if (rawState is null)
                return string.Empty;

            // Enum-typed (adapted console output): cast to its integer value
            // and fall through to the byte map below.
            if (rawState is Enum)
                rawState = Convert.ToByte(rawState);

            if (rawState is byte b)
                return b switch
                {
                    0 => "Unreachable",
                    1 => "Incomplete",
                    2 => "Probe",
                    3 => "Delay",
                    4 => "Stale",
                    5 => "Reachable",
                    6 => "Permanent",
                    7 => "Failed",
                    _ => string.Empty,
                };

            // String-typed (either an enum name such as "Permanent" or a
            // decimal digit such as "6", depending on who stringified it).
            string s = rawState.ToString() ?? string.Empty;
            if (byte.TryParse(s, out byte parsed))
                return NormalizeNeighborState(parsed);
            // Only the eight known names pass through; anything else (e.g. an
            // arbitrary object's type name) is not a neighbor state and must
            // not rank as usable.
            foreach (string known in new[] { "Unreachable", "Incomplete", "Probe", "Delay", "Stale", "Reachable", "Permanent", "Failed" })
            {
                if (s.Equals(known, StringComparison.OrdinalIgnoreCase))
                    return known;
            }
            return string.Empty;
        }

        /// <summary>
        /// Pure neighbor-table match, extracted for unit tests. Returns the
        /// IPv4 bound to any of <paramref name="vmMacs"/> (best state first:
        /// Reachable over Permanent over Delay over Stale), or null when no row
        /// matches. Incomplete/Failed/Unreachable rows and link-local (APIPA) addresses
        /// are excluded: neither can carry a usable SSH session. Placeholder
        /// MACs (all-zero / broadcast) are excluded from BOTH the candidate
        /// set and the match rows — such a MAC is shared by every host on the
        /// segment and binds to dead Unreachable rows, never to a usable guest.
        /// </summary>
        internal static string? PickNeighborMacMatch(
            System.Collections.Generic.IEnumerable<string> vmMacs,
            System.Collections.Generic.IEnumerable<(string Ip, string Mac, string State)> neighbors)
        {
            var wanted = new HashSet<string>(
                vmMacs.Select(NormalizeMacAddress).Where(IsUsableMacAddress),
                StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0)
                return null;

            foreach (string stateRank in new[] { "Reachable", "Permanent", "Delay", "Stale" })
            {
                foreach (var (ip, mac, state) in neighbors)
                {
                    if (!state.Equals(stateRank, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!IPv4Regex.IsMatch(ip) || ip.StartsWith("169.254.", StringComparison.Ordinal))
                        continue;
                    string rowMac = NormalizeMacAddress(mac);
                    if (!IsUsableMacAddress(rowMac))
                        continue;
                    if (wanted.Contains(rowMac))
                        return ip;
                }
            }

            return null;
        }

        /// <summary>
        /// True when a MAC (already normalized or not) is a real, non-shared
        /// hardware address. Rejects empty, all-zero placeholder, and broadcast
        /// MACs — none of which can identify a specific guest over ARP.
        /// </summary>
        internal static bool IsUsableMacAddress(string? mac)
        {
            string normalized = NormalizeMacAddress(mac);
            return normalized.Length == 12
                && normalized != "000000000000"
                && normalized != "FFFFFFFFFFFF";
        }

        /// <summary>
        /// Normalizes a MAC to uppercase hex without separators so that
        /// Hyper-V's "00155DA1B2C3" and NetNeighbor's "00-15-5d-a1-b2-c3"
        /// representations compare equal.
        /// </summary>
        internal static string NormalizeMacAddress(string? mac)
        {
            if (string.IsNullOrEmpty(mac))
                return string.Empty;
            var sb = new StringBuilder(12);
            foreach (char c in mac)
                if (char.IsAsciiHexDigit(c))
                    sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        private static string GetAdapterMacAddress(PSObject adapter)
            => adapter.Properties["MacAddress"]?.Value?.ToString() ?? string.Empty;

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
