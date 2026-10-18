using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    public class KvpBase : IVmShutdownWatcher
    {
        // WQL string literals are delimited by single quotes; reject any VM name
        // that could break out of or alter the query (quote or backslash).
        private static readonly Regex InvalidVmNameChars = new Regex(@"['\\]", RegexOptions.Compiled);

        /// <summary>
        /// Throws when <paramref name="vmName"/> contains characters that would
        /// corrupt the WQL queries used against Msvm_ComputerSystem.
        /// </summary>
        protected static void ValidateVmName(string vmName)
        {
            if (string.IsNullOrEmpty(vmName))
                throw new ArgumentException("VM name must not be null or empty.", nameof(vmName));
            if (InvalidVmNameChars.IsMatch(vmName))
                throw new ArgumentException($"VM name '{vmName}' contains characters that are invalid for WQL queries.", nameof(vmName));
        }
        /// <summary>
        /// Poll until VM is running (EnabledState = 2) and return GUID
        /// </summary>
        /// <param name="vmName"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="timeoutSeconds"></param>
        /// <param name="pollIntervalMs"></param>
        /// <returns></returns>
        public async Task<string?> WaitForVMRunningAsync(string vmName, CancellationToken cancellationToken, int timeoutSeconds = 300, int pollIntervalMs = 1000)
        {
            DateTime startTime = DateTime.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                string? guid = GetVMGuid(vmName);
                if (!string.IsNullOrEmpty(guid))
                {
                    return guid;
                }

                if ((DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds)
                {
                    return null;  // Timeout
                }

                await Task.Delay(pollIntervalMs, cancellationToken);
            }
            return null;
        }

        public async Task<bool> WaitForVMShutdownAsync(string vmName, CancellationToken cancellationToken, int pollIntervalMs = 1000)
        {
            return await WaitForVMShutdownAsync(vmName, cancellationToken, timeoutSeconds: 0, pollIntervalMs);
        }

        /// <summary>
        /// Wait for VM to shut down. Returns true if the VM shut down, false if
        /// the timeout expired while the VM was still running.
        /// Set timeoutSeconds=0 for no timeout (waits indefinitely until cancelled).
        /// </summary>
        public async Task<bool> WaitForVMShutdownAsync(string vmName, CancellationToken cancellationToken, int timeoutSeconds, int pollIntervalMs = 1000)
        {
            DateTime startTime = DateTime.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                string? guid = GetVMGuid(vmName);
                if (string.IsNullOrEmpty(guid))
                {
                    return true;
                }

                if (timeoutSeconds > 0 && (DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds)
                {
                    return false; // Timeout — VM still running
                }

                await Task.Delay(pollIntervalMs, cancellationToken);
            }
            return false;
        }

        /// <summary>
        /// Get VM GUID from WMI (returns null if not running).
        /// Virtual so host-less unit tests can substitute a fake GUID and
        /// exercise polling loops (e.g. <see cref="KvpHeartbeatGate"/>).
        /// </summary>
        /// <param name="vmName"></param>
        /// <returns></returns>
        protected virtual string? GetVMGuid(string vmName)
        {
            return ResolveVmGuid(vmName, runningOnly: true);
        }

        /// <summary>
        /// Public GUID resolution for transports: the vsock SSH target host
        /// string is the VM GUID, and callers need it before the VM is
        /// running (e.g. to build the shell before StartVM). State-blind
        /// unlike <see cref="GetVMGuid"/> — the GUID is immutable.
        /// </summary>
        public string? ResolveVmGuidAnyState(string vmName)
        {
            return ResolveVmGuid(vmName, runningOnly: false);
        }

        private string? ResolveVmGuid(string vmName, bool runningOnly)
        {
            ValidateVmName(vmName);

            ManagementScope scope = new ManagementScope(@"root\virtualization\v2");
            string filter = $"ElementName = '{vmName}'" + (runningOnly ? " AND EnabledState = 2" : "");
            ObjectQuery query = new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE {filter}");
            var matches = new List<string>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    {
                        // Name is the GUID
                        if (obj["Name"]?.ToString() is { } guid) matches.Add(guid);
                    }
                }
            }

            if (matches.Count > 1)
            {
                // Hyper-V permits duplicate VM names. The GUID is the vsock
                // host string — silently picking the first match would SSH
                // into the WRONG VM (per-boot host keys mean TOFU cannot
                // catch it either). Fail loudly instead.
                throw new InvalidOperationException(
                    $"VM name '{vmName}' matches {matches.Count} VMs ({string.Join(", ", matches)}). " +
                    "VMCreate requires unique VM names; rename or remove the duplicate before deploying.");
            }
            return matches.Count == 1 ? matches[0] : null;
        }
    }
}