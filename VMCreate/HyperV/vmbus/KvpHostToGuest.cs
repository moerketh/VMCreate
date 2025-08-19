using System;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    public class KvpHostToGuest : KvpBase
    {
        /// <summary>
        /// Async method to send a KVP from host to guest, waits for VM to be in a running state
        /// </summary>
        /// <param name="vmName"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public async Task SendKVPToGuestAsync(string vmName, string key, string value, CancellationToken cancellationToken = default)
        {
            // Poll for VM to be running and get GUID
            string vmGuid = await WaitForVMRunningAsync(vmName, cancellationToken);
            if (string.IsNullOrEmpty(vmGuid))
            {
                throw new Exception($"VM '{vmName}' did not start within the timeout or is not running.");
            }

            ManagementScope scope = new ManagementScope(@"root\virtualization\v2");
            // Get the virtual system management service
            ManagementPath servicePath = new ManagementPath("Msvm_VirtualSystemManagementService");
            using (ManagementClass serviceClass = new ManagementClass(scope, servicePath, null))
            {
                using (ManagementObject service = serviceClass.GetInstances().Cast<ManagementObject>().First())
                {
                    // Get the VM's ComputerSystem object
                    ObjectQuery vmQuery = new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmGuid}'");
                    using (ManagementObjectSearcher vmSearcher = new ManagementObjectSearcher(scope, vmQuery))
                    {
                        ManagementObject vm = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                        if (vm == null)
                        {
                            throw new Exception("VM ComputerSystem not found.");
                        }
                        string target = vm.Path.Path;

                        const int maxRetries = 5;
                        const int retryDelayMs = 5000; // 5 seconds
                        int retryCount = 0;

                        while (true)
                        {
                            try
                            {
                                // Create the KVP item instance
                                ManagementPath kvpPath = new ManagementPath("Msvm_KvpExchangeDataItem");
                                using (ManagementClass kvpClass = new ManagementClass(scope, kvpPath, null))
                                {
                                    using (ManagementObject kvpItem = kvpClass.CreateInstance())
                                    {
                                        string kvpXml = $@"<INSTANCE CLASSNAME=""Msvm_KvpExchangeDataItem"">
  <PROPERTY NAME=""Data"" TYPE=""string"">
    <VALUE>{value}</VALUE>
  </PROPERTY>
  <PROPERTY NAME=""Name"" TYPE=""string"">
    <VALUE>{key}</VALUE>
  </PROPERTY>
  <PROPERTY NAME=""Source"" TYPE=""uint16"">
    <VALUE>0</VALUE>
  </PROPERTY>
</INSTANCE>";
                                        string[] dataItems = new string[1];
                                        dataItems[0] = kvpXml;

                                        // Prepare parameters for AddKvpItems
                                        ManagementBaseObject inParams = service.GetMethodParameters("AddKvpItems");
                                        inParams["TargetSystem"] = target;
                                        inParams["DataItems"] = dataItems;

                                        // Invoke the method
                                        ManagementBaseObject outParams = service.InvokeMethod("AddKvpItems", inParams, null);
                                        uint returnValue = (uint)outParams["ReturnValue"];

                                        if (returnValue == 4096) // Job started (async)
                                        {
                                            string jobPath = (string)outParams["Job"];
                                            if (string.IsNullOrEmpty(jobPath))
                                            {
                                                throw new Exception("Job started but Job path is null or empty.");
                                            }
                                            using (ManagementObject job = new ManagementObject(scope, new ManagementPath(jobPath), null))
                                            {
                                                if (!await WaitForJobCompletionAsync(job, cancellationToken))
                                                {
                                                    throw new Exception("Failed to add KVP: Job did not complete successfully.");
                                                }
                                            }
                                        }
                                        else if (returnValue != 0)
                                        {
                                            throw new Exception($"Failed to add KVP: Return code {returnValue}");
                                        }
                                    }
                                }
                                // If we reach here, success - break out of retry loop
                                break;
                            }
                            catch (Exception ex)
                            {
                                if (retryCount >= maxRetries || !IsRetryableError(ex))
                                {
                                    throw;
                                }
                                retryCount++;
                                await Task.Delay(retryDelayMs, cancellationToken);
                            }
                        }
                    }
                }
            }
        }

        // Helper to wait for WMI job completion (updated to take ManagementObject)
        private async Task<bool> WaitForJobCompletionAsync(ManagementObject job, CancellationToken cancellationToken, int pollIntervalMs = 1000, int timeoutSeconds = 60)
        {
            DateTime startTime = DateTime.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                job.Get(); // Refresh job state
                ushort jobState = (ushort)job["JobState"];
                if (jobState == 7) // Completed successfully (7 = Completed)
                {
                    return true;
                }
                else if (jobState > 7 && jobState != 10) // Failed or other error states
                {
                    string errorDesc = job["ErrorDescription"]?.ToString() ?? "Unknown error";
                    throw new Exception($"Job failed: {errorDesc} (ErrorCode: {job["ErrorCode"]})");
                }
                if ((DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds)
                {
                    throw new Exception("Job timed out.");
                }
                await Task.Delay(pollIntervalMs, cancellationToken);
            }
            return false;
        }

        // Helper to determine if the error is retryable (e.g., transient "device not ready")
        private bool IsRetryableError(Exception ex)
        {
            string msg = ex.Message.ToLower();
            return msg.Contains("0x800710df") || msg.Contains("the device is not ready for use");
        }
    }
}