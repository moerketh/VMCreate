using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace VMCreate
{
    public class HyperVKVPPoller
    {
        // Async method to poll KVP and report progress
        public async Task PollKVPForProgressAsync(string vmName, IProgress<CreateVMProgressInfo> progressReporter, CancellationToken cancellationToken = default)
        {
            // Initial report
            var initialInfo = new CreateVMProgressInfo
            {
                ProgressPercentage = 0,
                Phase = "Waiting for VM...",
                URI = string.Empty,
                DownloadSpeed = -1
            };
            progressReporter.Report(initialInfo);

            // Poll for VM to be running and get GUID
            string vmGuid = await WaitForVMRunningAsync(vmName, cancellationToken);
            if (string.IsNullOrEmpty(vmGuid))
            {
                throw new Exception($"VM '{vmName}' did not start within the timeout or is not running.");
            }

            var vmRunningInfo = new CreateVMProgressInfo
            {
                ProgressPercentage = 0,
                Phase = $"VM {vmName} running. Waiting for disk clone...",
                URI = $"VM GUID: {vmGuid}",
                DownloadSpeed = -1
            };
            progressReporter.Report(vmRunningInfo);

            // Poll loop for KVP (every 5 seconds; adjust interval as needed)
            while (!cancellationToken.IsCancellationRequested)
            {
                Dictionary<string, string> customKvps = GetCustomKVPs(vmGuid);

                if (customKvps.TryGetValue("PartcloneProgress", out string progressValue) && !string.IsNullOrEmpty(progressValue))
                {
                    var info = ParseProgressValue(progressValue);

                    progressReporter.Report(info);

                    // Check for completion
                    if (progressValue.Contains("Completed: 100% | Done"))
                    {
                        break;
                    }
                }

                await Task.Delay(1000, cancellationToken);  // Poll interval
            }
        }

        // Poll until VM is running (EnabledState = 2) and return GUID
        private async Task<string> WaitForVMRunningAsync(string vmName, CancellationToken cancellationToken, int timeoutSeconds = 300, int pollIntervalMs = 5000)
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

        // Get VM GUID from WMI (returns null if not running)
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

        // Get custom KVPs from GuestExchangeItems
        private Dictionary<string, string> GetCustomKVPs(string vmGuid)
        {
            var kvps = new Dictionary<string, string>();

            ManagementScope scope = new ManagementScope(@"root\virtualization\v2");
            ObjectQuery query = new ObjectQuery($"SELECT * FROM Msvm_KvpExchangeComponent WHERE SystemName = '{vmGuid}'");
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    string[] items = (string[])obj["GuestExchangeItems"];
                    if (items != null)
                    {
                        foreach (string item in items)
                        {
                            // Parse XML
                            XDocument xml = XDocument.Parse(item);
                            var nameProp = xml.Descendants("PROPERTY").FirstOrDefault(p => (string)p.Attribute("NAME") == "Name");
                            var dataProp = xml.Descendants("PROPERTY").FirstOrDefault(p => (string)p.Attribute("NAME") == "Data");

                            if (nameProp != null && dataProp != null)
                            {
                                string kvpKey = nameProp.Element("VALUE")?.Value;
                                string kvpValue = dataProp.Element("VALUE")?.Value;
                                if (!string.IsNullOrEmpty(kvpKey))
                                {
                                    kvps[kvpKey] = kvpValue;
                                }
                            }
                        }
                    }
                }
            }
            return kvps;
        }

        // Parse the progress string into CreateVMProgressInfo
        private CreateVMProgressInfo ParseProgressValue(string progressValue)
        {
            var info = new CreateVMProgressInfo
            {
                Phase = "Cloning disk...",
                URI = string.Empty,
                DownloadSpeed = -1
            };

            // Split parts (e.g., "Progress: 50% | Rate: 10 GB/min | Blocks: 1000/2000")
            string[] parts = progressValue.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                if (part.StartsWith("Progress: ") || part.StartsWith("Completed: "))
                {
                    string percentStr = part.Split(':')[1].Trim().Replace("%", "");
                    if (double.TryParse(percentStr, new CultureInfo("en-US"), out double percent))
                    {
                        info.ProgressPercentage = Convert.ToInt32(percent);
                    }
                }
                else if (part.StartsWith("Rate: "))
                {
                    string rateStr = part.Split(':')[1].Trim().Replace(" GB/min", "");
                    if (double.TryParse(rateStr, new CultureInfo("en-US"), out double rateGbMin))
                    {
                        info.DownloadSpeed = rateGbMin * 1000 / 60;  // Convert GB/min to MB/s
                    }
                }
                // Ignore Blocks for now, but could add to URI if needed
            }

            return info;
        }

        // Async method to send a KVP from host to guest, waiting for VM to be running
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

                        // Create the KVP item instance
                        ManagementPath kvpPath = new ManagementPath("Msvm_KvpExchangeDataItem");
                        using (ManagementClass kvpClass = new ManagementClass(scope, kvpPath, null))
                        {
                            using (ManagementObject kvpItem = kvpClass.CreateInstance())
                            {
                                //kvpItem["Data"] = value;
                                //kvpItem["Name"] = key;  // Property for the key
                                //kvpItem["Source"] = 0;  // 0 indicates host-origin

                                // Inside the using (ManagementObject kvpItem = kvpClass.CreateInstance()) block, replace the serialization with this:
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

                                if (returnValue == 4096)  // Job started (async)
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
                job.Get();  // Refresh job state
                ushort jobState = (ushort)job["JobState"];

                if (jobState == 7)  // Completed successfully (7 = Completed)
                {
                    return true;
                }
                else if (jobState > 7 && jobState != 10)  // Failed or other error states
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
    }
}