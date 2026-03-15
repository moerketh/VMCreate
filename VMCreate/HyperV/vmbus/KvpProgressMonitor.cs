using CreateVM.HyperV.vmbus;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace VMCreate
{
    public class HyperVKVPPoller : KvpBase, IKvpPoller
    {
        /// <summary>
        /// Async method to poll KVP and report progress
        /// </summary>
        /// <param name="vmName"></param>
        /// <param name="progressReporter"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public async Task<bool> PollKVPForProgressAsync(string vmName, IProgress<CreateVMProgressInfo> progressReporter, CancellationToken cancellationToken, int timeoutSeconds = 600)
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

            DateTime startTime = DateTime.UtcNow;

            // Poll loop for KVP with timeout and VM-shutdown detection
            while (!cancellationToken.IsCancellationRequested)
            {
                // If the VM shut down, the clone must have completed even
                // if we missed the KVP completion marker.
                string currentGuid = GetVMGuid(vmName);
                if (string.IsNullOrEmpty(currentGuid))
                    return true;

                // Timeout — let the caller fall through to diagnostics collection
                if (timeoutSeconds > 0 && (DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds)
                    return false;

                Dictionary<string, string> customKvps = GetCustomKVPs(vmGuid);

                if (customKvps.TryGetValue("PartcloneProgress", out string progressValue) && !string.IsNullOrEmpty(progressValue))
                {
                    var info = ParseProgressValue(progressValue);

                    progressReporter.Report(info);

                    // Check for completion
                    if (progressValue.Contains("Completed: 100% | Done"))
                    {
                        return true;
                    }
                }
                await Task.Delay(1000, cancellationToken);
            }
            return false;
        }

        /// <summary>
        /// Polls WorkflowProgress KVP while waiting for VM shutdown.
        /// Used for Gen2 customize-only flows where there is no partclone step.
        /// Reports progress text updates to the UI via the Customize phase card.
        /// Returns true if the VM shut down cleanly, false on timeout.
        /// </summary>
        public async Task<bool> WaitForShutdownWithProgressAsync(
            string vmName,
            IProgress<CreateVMProgressInfo> progressReporter,
            CancellationToken cancellationToken,
            int timeoutSeconds = 600)
        {
            string vmGuid = await WaitForVMRunningAsync(vmName, cancellationToken);
            if (string.IsNullOrEmpty(vmGuid))
                return true; // VM already off

            DateTime startTime = DateTime.UtcNow;
            string lastProgress = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Check if VM is still running
                string currentGuid = GetVMGuid(vmName);
                bool vmOff = string.IsNullOrEmpty(currentGuid);

                // Always do a KVP read (even after shutdown) so we don't
                // miss the last few progress updates (e.g. SSH_SETUP, REBOOT).
                try
                {
                    var kvps = GetCustomKVPs(vmGuid);
                    if (kvps.TryGetValue("WorkflowProgress", out string progress)
                        && !string.IsNullOrEmpty(progress)
                        && progress != lastProgress)
                    {
                        lastProgress = progress;
                        progressReporter.Report(new CreateVMProgressInfo
                        {
                            Phase = "Customize",
                            URI = progress
                        });
                    }
                }
                catch
                {
                    // KVP read may fail transiently while VM is shutting down
                }

                if (vmOff)
                    return true; // VM shut down successfully

                // Check timeout
                if (timeoutSeconds > 0 && (DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds)
                    return false; // Timeout — VM still running

                await Task.Delay(2000, cancellationToken);
            }
            return false;
        }

        /// <summary>
        /// Get custom KVPs from GuestExchangeItems
        /// </summary>
        /// <param name="vmGuid"></param>
        /// <returns></returns>
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

        /// <summary>
        /// Parse the progress string into CreateVMProgressInfo
        /// </summary>
        /// <param name="progressValue"></param>
        /// <returns></returns>
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
                    // Rate string may be "10.24GB/min", "10235392.00MB/min", or "5.07 GB/min".
                    string rateStr = part.Substring("Rate: ".Length).Trim();
                    var rateMatch = Regex.Match(rateStr, @"([\d.]+)\s*(GB|MB|KB)/min");
                    if (rateMatch.Success
                        && double.TryParse(rateMatch.Groups[1].Value, CultureInfo.InvariantCulture, out double rateValue))
                    {
                        info.DownloadSpeed = rateMatch.Groups[2].Value switch
                        {
                            "GB" => rateValue * 1000 / 60,   // GB/min → MB/s
                            "MB" => rateValue / 60,           // MB/min → MB/s
                            "KB" => rateValue / 1024 / 60,    // KB/min → MB/s
                            _ => -1
                        };
                    }
                }
            }

            return info;
        }
    }
}