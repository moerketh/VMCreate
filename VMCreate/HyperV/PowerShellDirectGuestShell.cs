using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Management.Automation;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        private readonly string _password;
        private readonly PSCredential _credential;

        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(600);
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

        public string VmName => _vmName;

        public PowerShellDirectGuestShell(ILogger logger, string vmName, string username, string password)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _vmName = vmName ?? throw new ArgumentNullException(nameof(vmName));
            _username = username ?? throw new ArgumentNullException(nameof(username));
            _password = password ?? throw new ArgumentNullException(nameof(password));

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
        /// </summary>
        public async Task WaitForReadyAsync(CancellationToken ct)
        {
            _logger.LogInformation("Waiting for PowerShell Direct to become available on VM {VMName}...", _vmName);

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
                    string result = await RunCommandInternalAsync("Write-Output 'ps-direct-ready'", TimeSpan.FromSeconds(15), ct);
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
            _logger.LogDebug("Running PowerShell Direct command on VM {VMName}: {Command}", _vmName, Truncate(command, 200));
            string result = await RunCommandInternalAsync(command, CommandTimeout, ct);
            _logger.LogDebug("PowerShell Direct command completed on VM {VMName} ({Length} chars)", _vmName, result?.Length ?? 0);
            return result;
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

        private async Task<string> RunCommandInternalAsync(string script, TimeSpan timeout, CancellationToken ct)
        {
            using var ps = PowerShell.Create();
            ps.AddCommand("Import-Module").AddParameter("Name", "Hyper-V").Invoke();
            ps.Commands.Clear();

            // Use Invoke-Command to run the script inside the VM via PowerShell Direct
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

        private static string Truncate(string s, int maxLength)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= maxLength ? s : s.Substring(0, maxLength) + "...";
        }

        private static string EscapeForPowerShell(string s)
        {
            return s.Replace("'", "''");
        }
    }
}