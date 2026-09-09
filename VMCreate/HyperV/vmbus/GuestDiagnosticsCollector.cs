using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

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
        // "ubuntu", NOT SshGuestShell's "vmcreate": this collector targets
        // the ISO boot-cycle guest (the debootstrap environment booted from
        // the cloning ISO), whose SSH access is set up for the default live
        // user. It runs while the deployment target is still being cloned —
        // the vmcreate automation user only exists on the deployed disk,
        // after the disk is first booted from the real OS.
        private const string GuestUsername = "ubuntu";
        private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(30);

        public GuestDiagnosticsCollector(ILogger<GuestDiagnosticsCollector> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Connects to the ISO guest via SSH and collects autorun
        /// service status, journal output, mount state, and recent kernel messages.
        /// Returns a structured diagnostics string, or an error message if the
        /// connection itself fails.
        /// </summary>
        public async Task<GuestDiagnostics> CollectAsync(string vmName, CancellationToken ct, string privateKeyPath = null)
        {
            _logger.LogInformation("Collecting diagnostics from ISO guest via SSH for VM: {VMName}", vmName);

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
                _logger.LogWarning(ex, "Failed to collect guest diagnostics via SSH: {Message}", ex.Message);
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
        /// Delegates to the shared <see cref="SshTransport"/> (same transport
        /// as SshGuestShell) and re-adds the diagnostics-only tolerance around
        /// it: partial output beats a clean failure when the guest is already
        /// in a bad state.
        /// </summary>
        private async Task<string> RunGuestCommandAsync(string vmName, string linuxCommand, CancellationToken ct, string privateKeyPath = null)
        {
            if (string.IsNullOrEmpty(privateKeyPath) || !System.IO.File.Exists(privateKeyPath))
                throw new InvalidOperationException("SSH private key path is required for guest diagnostics collection.");

            // Discover the VM's IP address via Get-VMNetworkAdapter
            string vmIp = await SshTransport.DiscoverVmIpAsync(vmName, ct, preferVmCreateTempAdapter: false);
            if (string.IsNullOrEmpty(vmIp))
                throw new InvalidOperationException($"Could not discover IP address for VM '{vmName}'. Guest networking may not be ready.");

            _logger.LogDebug("Discovered VM IP {IP} for diagnostics on {VMName}", vmIp, vmName);

            // NOTE: never log the full ssh argument list — the remote command
            // can embed guest payload, and the plaintext rolling log lives
            // in %TEMP%. Transport options only.
            _logger.LogDebug("SSH diagnostics exec on {VMName}@{IP} ({Length} chars)", vmName, vmIp, linuxCommand.Length);

            // knownHostsFile: null — NO host-key pinning here. The ISO-cycle
            // guest is a transient boot identity (its host key is the live
            // ISO environment's, never the deployed disk's), so a pinned file
            // would store a key that outlives its owner. This exec keeps
            // StrictHostKeyChecking=no with entries discarded to NUL.
            try
            {
                return await SshTransport.ExecuteAsync(
                    _logger, vmName, privateKeyPath, vmIp, GuestUsername,
                    knownHostsFile: null, linuxCommand, SshTimeout, ct,
                    tolerateNonZeroExit: true);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"SSH diagnostics timed out after {SshTimeout.TotalSeconds}s — guest may not have sshd running or network is unreachable.");
            }
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

        /// <summary>True if the SSH connection succeeded and data was collected.</summary>
        public bool CollectedSuccessfully { get; set; }
    }
}
