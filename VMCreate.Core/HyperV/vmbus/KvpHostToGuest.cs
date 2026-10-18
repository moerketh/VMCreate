using System;
using System.Linq;
using System.Management;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    public class KvpHostToGuest : KvpBase, IKvpSender, IKvpMaintenance
    {
        /// <summary>
        /// Builds the embedded-XML form of a host-sent (Source=0) KVP item.
        /// AddKvpItems/ModifyKvpItems/RemoveKvpItems all take DataItems as
        /// these XML strings — the WMI-object form silently fails on Remove
        /// (live-verified: JobState 10 with no diagnostic).
        /// </summary>
        private static string BuildItemXml(string name, string? data) =>
            $@"<INSTANCE CLASSNAME=""Msvm_KvpExchangeDataItem"">
  <PROPERTY NAME=""Data"" TYPE=""string"">
    <VALUE>{SecurityElement.Escape(data ?? string.Empty)}</VALUE>
  </PROPERTY>
  <PROPERTY NAME=""Name"" TYPE=""string"">
    <VALUE>{SecurityElement.Escape(name)}</VALUE>
  </PROPERTY>
  <PROPERTY NAME=""Source"" TYPE=""uint16"">
    <VALUE>0</VALUE>
  </PROPERTY>
</INSTANCE>";

        /// <summary>Removes a host-sent KVP item. Live-verified semantics:
        /// works with the VM powered off; the Data must match what is stored
        /// when provided.</summary>
        public async Task RemoveKvpItemAsync(string vmName, string name, string? data, CancellationToken ct = default)
        {
            ValidateVmName(vmName);
            await InvokeItemsMethodAsync(vmName, "RemoveKvpItems", BuildItemXml(name, data), ct);
        }

        /// <summary>Modifies an existing host-sent KVP item in place. Used by
        /// the v2 cycle's Add-then-Modify replace semantics (Add fails on a
        /// duplicate Name; JobState 10 is generic, so Modify is attempted on
        /// any Add failure and the original Add error surfaces if both fail).
        /// </summary>
        public async Task ModifyKvpItemAsync(string vmName, string name, string data, CancellationToken ct = default)
        {
            ValidateVmName(vmName);
            await InvokeItemsMethodAsync(vmName, "ModifyKvpItems", BuildItemXml(name, data), ct);
        }

        /// <summary>Resolves the VM GUID regardless of power state (the
        /// vsock SSH host string). Throws when the name is ambiguous.</summary>
        public Task<string?> ResolveVmGuidAsync(string vmName, CancellationToken ct = default)
            => Task.FromResult(ResolveVmGuidAnyState(vmName));

        /// <summary>
        /// Shared plumbing for the Add/Modify/Remove item methods: resolves
        /// the VM GUID FIRST (duplicate names throw instead of silently
        /// writing to the wrong VM), queries by GUID, invokes the method with
        /// one embedded-XML DataItem, and awaits the WMI job via
        /// WaitForJobCompletionAsync (correct failure states 8-11 + its own
        /// 60 s bound — a hand-rolled loop here polled forever on JobState
        /// 10 in review).
        /// </summary>
        private async Task InvokeItemsMethodAsync(string vmName, string method, string itemXml, CancellationToken ct)
        {
            // GUID-first: also protects against duplicate-name wrong-VM writes.
            string? vmGuid = ResolveVmGuidAnyState(vmName)
                ?? throw new Exception($"VM '{vmName}' not found for {method}.");
            ct.ThrowIfCancellationRequested();

            ManagementScope scope = new ManagementScope(@"root\virtualization\v2");
            ObjectQuery vmQuery = new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmGuid}'");
            using (ManagementObjectSearcher vmSearcher = new ManagementObjectSearcher(scope, vmQuery))
            using (ManagementObjectCollection vmResults = vmSearcher.Get())
            {
                ManagementObject? vm = vmResults.Cast<ManagementObject>().FirstOrDefault();
                if (vm == null)
                    throw new Exception($"VM '{vmName}' (GUID {vmGuid}) not found for {method}.");
                string? target = vm.Path?.Path;
                vm.Dispose();

                ManagementPath servicePath = new ManagementPath("Msvm_VirtualSystemManagementService");
                using (ManagementClass serviceClass = new ManagementClass(scope, servicePath, null!))
                using (ManagementObject service = serviceClass.GetInstances().Cast<ManagementObject>().First())
                {
                    ManagementBaseObject inParams = service.GetMethodParameters(method);
                    inParams["TargetSystem"] = new ManagementPath(target);
                    inParams["DataItems"] = new string[] { itemXml };
                    ManagementBaseObject outParams = await Task.Run(() => service.InvokeMethod(method, inParams, null), ct);

                    uint returnValue = (uint)outParams["ReturnValue"]!;
                    if (returnValue == 0)
                        return;
                    if (returnValue != 4096)
                        throw new Exception($"{method} failed: return code {returnValue}");

                    string? jobPath = (string?)outParams["Job"];
                    if (string.IsNullOrEmpty(jobPath))
                        throw new Exception($"{method} started a job but no job path was returned.");
                    using (ManagementObject job = new ManagementObject(scope, new ManagementPath(jobPath), null))
                    {
                        await WaitForJobCompletionAsync(job, ct);
                    }
                }
            }
        }

        /// <summary>
        /// Async method to send a KVP from host to guest, waits for VM to be in a running state
        /// </summary>
        /// <param name="vmName"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public async Task SendKVPToGuestAsync(string vmName, string key, string? value, CancellationToken cancellationToken = default)
        {
            ValidateVmName(vmName);
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("KVP key must not be null or empty.", nameof(key));

            // Poll for VM to be running and get GUID
            string? vmGuid = await WaitForVMRunningAsync(vmName, cancellationToken);
            if (string.IsNullOrEmpty(vmGuid))
            {
                throw new Exception($"VM '{vmName}' did not start within the timeout or is not running.");
            }

            ManagementScope scope = new ManagementScope(@"root\virtualization\v2");
            // Get the virtual system management service
            ManagementPath servicePath = new ManagementPath("Msvm_VirtualSystemManagementService");
            using (ManagementClass serviceClass = new ManagementClass(scope, servicePath, null!))            {
                using (ManagementObject service = serviceClass.GetInstances().Cast<ManagementObject>().First())                {
                    // Get the VM's ComputerSystem object
                    ObjectQuery vmQuery = new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmGuid}'");
                    using (ManagementObjectSearcher vmSearcher = new ManagementObjectSearcher(scope, vmQuery))
                    using (ManagementObjectCollection vmResults = vmSearcher.Get())
                    {
                        ManagementObject? vm = vmResults.Cast<ManagementObject>().FirstOrDefault();
                        if (vm == null)
                        {
                            throw new Exception("VM ComputerSystem not found.");
                        }
                        string? target = vm.Path?.Path;
                        vm.Dispose();

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
    <VALUE>{SecurityElement.Escape(value ?? string.Empty)}</VALUE>
  </PROPERTY>
  <PROPERTY NAME=""Name"" TYPE=""string"">
    <VALUE>{SecurityElement.Escape(key)}</VALUE>
  </PROPERTY>
  <PROPERTY NAME=""Source"" TYPE=""uint16"">
    <VALUE>0</VALUE>
  </PROPERTY>
</INSTANCE>";
                                        string[] dataItems = new string[1];
                                        dataItems[0] = kvpXml;

                                        // Prepare parameters for AddKvpItems
                                        ManagementBaseObject inParams = service.GetMethodParameters("AddKvpItems");
                                        inParams["TargetSystem"] = target!;
                                        inParams["DataItems"] = dataItems;

                                        // Invoke the method
                                        ManagementBaseObject outParams = service.InvokeMethod("AddKvpItems", inParams, null);
                                        uint returnValue = (uint)outParams["ReturnValue"]!;

                                        if (returnValue == 4096) // Job started (async)
                                        {
                                            string? jobPath = (string?)outParams["Job"];
                                            if (string.IsNullOrEmpty(jobPath))
                                            {
                                                throw new Exception("Job started but Job path is null or empty.");
                                            }
                                            using (ManagementObject job = new ManagementObject(scope, new ManagementPath(jobPath), null))
                                            {
                                                await WaitForJobCompletionAsync(job, cancellationToken);
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
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                job.Get(); // Refresh job state
                // CIM_ConcreteJob.JobState: 2=New, 3=Starting, 4=Running, 5=Suspended,
                // 6=ShuttingDown, 7=Completed, 8=Terminated, 9=Killed, 10=Exception, 11=Service
                ushort jobState = (ushort)job["JobState"];
                if (jobState == 7)
                {
                    return true;
                }
                if (jobState is >= 8 and <= 11)
                {
                    string errorDesc = job["ErrorDescription"]?.ToString() ?? "Unknown error";
                    throw new Exception($"Job failed: {errorDesc} (ErrorCode: {job["ErrorCode"]}, JobState: {jobState})");
                }
                if ((DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds)
                {
                    throw new Exception("Job timed out.");
                }
                await Task.Delay(pollIntervalMs, cancellationToken);
            }
        }

        // Helper to determine if the error is retryable (e.g., transient "device not ready")
        private bool IsRetryableError(Exception ex)
        {
            string msg = ex.Message.ToLower();
            return msg.Contains("0x800710df") || msg.Contains("the device is not ready for use");
        }
    }
}