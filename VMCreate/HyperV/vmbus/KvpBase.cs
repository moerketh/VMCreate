using System;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    public class KvpBase
    {
        /// <summary>
        /// Poll until VM is running (EnabledState = 2) and return GUID
        /// </summary>
        /// <param name="vmName"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="timeoutSeconds"></param>
        /// <param name="pollIntervalMs"></param>
        /// <returns></returns>
        public async Task<string> WaitForVMRunningAsync(string vmName, CancellationToken cancellationToken, int timeoutSeconds = 300, int pollIntervalMs = 1000)
        {
            DateTime startTime = DateTime.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                string guid = GetVMGuid(vmName);
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

        public async Task<bool> WaitForVMShutdownAsync(string vmName, CancellationToken cancellationToken, int timeoutSeconds = 300, int pollIntervalMs = 1000)
        {
            DateTime startTime = DateTime.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                string guid = GetVMGuid(vmName);
                if (string.IsNullOrEmpty(guid))
                {
                    return true;
                }
                if ((DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds)
                {
                    return false; // Timeout
                }
                await Task.Delay(pollIntervalMs, cancellationToken);
            }
            return false;
        }

        /// <summary>
        /// Get VM GUID from WMI (returns null if not running)
        /// </summary>
        /// <param name="vmName"></param>
        /// <returns></returns>
        private string GetVMGuid(string vmName)
        {
            ManagementScope scope = new ManagementScope(@"root\virtualization\v2");
            ObjectQuery query = new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName}' AND EnabledState = 2");  // Use ElementName for friendly name; 2 = running
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    return obj["Name"].ToString();  // Name is the GUID
                }
            }
            return null;
        }
    }
}