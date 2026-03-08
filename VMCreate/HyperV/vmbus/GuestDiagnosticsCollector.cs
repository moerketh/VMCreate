using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Management.Automation;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    /// <summary>
    /// Collects diagnostic information from the ISO guest via PowerShell Direct
    /// (Invoke-Command over VMBus/hv_sock). Used when the customization workflow
    /// stalls or fails — the host reaches into the guest, pulls logs, and reports
    /// back before force-stopping the VM.
    /// 
    /// Requirements (on the ISO guest):
    ///   - openssh-server running
    ///   - PowerShell (pwsh) installed with SSH subsystem configured
    ///   - hv_sock kernel module (built into linux-azure)
    ///   - Password auth enabled for ubuntu/ubuntu credentials
    /// </summary>
    public class GuestDiagnosticsCollector
    {
        private readonly ILogger _logger;
        private const string GuestUsername = "ubuntu";
        private const string GuestPassword = "ubuntu";
        private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(30);

        public GuestDiagnosticsCollector(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Connects to the ISO guest via PowerShell Direct and collects autorun
        /// service status, journal output, mount state, and recent kernel messages.
        /// Returns a structured diagnostics string, or an error message if the
        /// connection itself fails.
        /// </summary>
        public async Task<GuestDiagnostics> CollectAsync(string vmName, CancellationToken ct)
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
                ", ct);

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
        /// Executes a command inside the guest VM via Invoke-Command -VMName.
        /// Uses PowerShell SDK to run the cmdlet, which connects over VMBus (hv_sock).
        /// </summary>
        private async Task<string> RunGuestCommandAsync(string vmName, string linuxCommand, CancellationToken ct)
        {
            using var ps = PowerShell.Create();

            var securePassword = new SecureString();
            foreach (char c in GuestPassword) securePassword.AppendChar(c);
            securePassword.MakeReadOnly();

            var credential = new PSCredential(GuestUsername, securePassword);

            // Build: Invoke-Command -VMName $vmName -Credential $cred -ScriptBlock { ... }
            ps.AddCommand("Invoke-Command")
              .AddParameter("VMName", vmName)
              .AddParameter("Credential", credential)
              .AddParameter("ScriptBlock", ScriptBlock.Create(linuxCommand))
              .AddParameter("ErrorAction", "Stop");

            // Run with timeout — PS Direct can hang if the guest is unresponsive
            var invokeTask = Task.Run(() => ps.Invoke(), ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(InvokeTimeout);

            var completedTask = await Task.WhenAny(invokeTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (completedTask != invokeTask)
            {
                ps.Stop(); // Force-stop the hung command
                throw new TimeoutException($"PowerShell Direct timed out after {InvokeTimeout.TotalSeconds}s — guest may not have pwsh installed or SSH is not responding.");
            }

            var results = await invokeTask;

            if (ps.HadErrors)
            {
                string errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                throw new Exception($"PowerShell Direct returned errors: {errors}");
            }

            var sb = new StringBuilder();
            foreach (var result in results)
            {
                sb.AppendLine(result?.ToString());
            }
            return sb.ToString();
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
