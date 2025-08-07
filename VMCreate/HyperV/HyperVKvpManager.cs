using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Xml.Linq;
using VMCreate;

namespace VMCreate
{
    public class HyperVKvpManager
    {
        private readonly ManagementScope _scope;
        private readonly IProgress<CreateVMProgressInfo> _progress; // Optional for error reporting

        /// <summary>
        /// Initializes a new instance of the HyperVKvpManager class.
        /// </summary>
        /// <param name="progress">Optional IProgress for reporting errors.</param>
        public HyperVKvpManager(IProgress<CreateVMProgressInfo> progress = null)
        {
            _scope = new ManagementScope(@"\\.\root\virtualization\v2");
            _progress = progress;
        }

        /// <summary>
        /// Sets a key-value pair for the specified VM in the extrinsic (host-to-guest) pool.
        /// </summary>
        /// <param name="vmName">The name of the Hyper-V VM.</param>
        /// <param name="key">The KVP key (e.g., "VMCREATE_DEBUG").</param>
        /// <param name="value">The KVP value (e.g., "true").</param>
        public async Task SetKvpAsync(string vmName, string key, string value)
        {
            try
            {
                var query = new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE ElementName='{vmName}'");
                using (var searcher = new ManagementObjectSearcher(_scope, query))
                {
                    var vmCollection = searcher.Get();

                    foreach (ManagementObject vm in vmCollection)
                    {
                        var kvpComponents = vm.GetRelated("Msvm_KvpExchangeComponent");
                        foreach (ManagementObject kvp in kvpComponents)
                        {
                            // Invoke AddGuestKvpItem directly with parameters: key, value, "extrinsic"
                            kvp.InvokeMethod("AddGuestKvpItem", new object[] { key, value, "extrinsic" });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_progress != null)
                {
                    _progress.Report(new CreateVMProgressInfo
                    {
                        ProgressPercentage = 0,
                        Phase = "Error",
                        URI = $"Failed to set KVP '{key}': {ex.Message}"
                    });
                }
                else
                {
                    throw; // Rethrow if no progress reporter
                }
            }
        }

        /// <summary>
        /// Reads all intrinsic (guest-to-host) key-value pairs for the specified VM.
        /// </summary>
        /// <param name="vmName">The name of the Hyper-V VM.</param>
        /// <returns>A dictionary of key-value pairs.</returns>
        public async Task<Dictionary<string, string>> GetIntrinsicKvpsAsync(string vmName)
        {
            var kvps = new Dictionary<string, string>();
            try
            {
                var query = new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE ElementName='{vmName}'");
                using (var searcher = new ManagementObjectSearcher(_scope, query))
                {
                    var vmCollection = searcher.Get();

                    foreach (ManagementObject vm in vmCollection)
                    {
                        var kvpComponents = vm.GetRelated("Msvm_KvpExchangeComponent");
                        foreach (ManagementObject kvp in kvpComponents)
                        {
                            string[] items = (string[])kvp["GuestIntrinsicExchangeItems"];
                            if (items != null)
                            {
                                foreach (string item in items)
                                {
                                    var xml = XElement.Parse(item);
                                    string keyName = xml.Element("Name")?.Value;
                                    string value = xml.Element("Data")?.Value;
                                    if (!string.IsNullOrEmpty(keyName))
                                    {
                                        kvps[keyName] = value ?? string.Empty;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_progress != null)
                {
                    _progress.Report(new CreateVMProgressInfo
                    {
                        ProgressPercentage = 0,
                        Phase = "Error",
                        URI = $"Failed to read intrinsic KVPs: {ex.Message}"
                    });
                }
                else
                {
                    throw; // Rethrow if no progress reporter
                }
            }
            return kvps;
        }

        /// <summary>
        /// Reads all extrinsic (host-to-guest) key-value pairs for the specified VM.
        /// </summary>
        /// <param name="vmName">The name of the Hyper-V VM.</param>
        /// <returns>A dictionary of key-value pairs.</returns>
        public async Task<Dictionary<string, string>> GetExtrinsicKvpsAsync(string vmName)
        {
            var kvps = new Dictionary<string, string>();
            try
            {
                var query = new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE ElementName='{vmName}'");
                using (var searcher = new ManagementObjectSearcher(_scope, query))
                {
                    var vmCollection = searcher.Get();

                    foreach (ManagementObject vm in vmCollection)
                    {
                        var kvpComponents = vm.GetRelated("Msvm_KvpExchangeComponent");
                        foreach (ManagementObject kvp in kvpComponents)
                        {
                            string[] items = (string[])kvp["GuestExchangeItems"];
                            if (items != null)
                            {
                                foreach (string item in items)
                                {
                                    var xml = XElement.Parse(item);
                                    string keyName = xml.Element("Name")?.Value;
                                    string value = xml.Element("Data")?.Value;
                                    if (!string.IsNullOrEmpty(keyName))
                                    {
                                        kvps[keyName] = value ?? string.Empty;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_progress != null)
                {
                    _progress.Report(new CreateVMProgressInfo
                    {
                        ProgressPercentage = 0,
                        Phase = "Error",
                        URI = $"Failed to read extrinsic KVPs: {ex.Message}"
                    });
                }
                else
                {
                    throw; // Rethrow if no progress reporter
                }
            }
            return kvps;
        }

        // Method to send a KVP from host to guest
        public void SendKVPToGuest(string vmName, string key, string value)
        {
            ManagementScope scope = new ManagementScope(@"root\virtualization\v2");

            // Get the virtual system management service
            ManagementPath servicePath = new ManagementPath("Msvm_VirtualSystemManagementService");
            using (ManagementClass serviceClass = new ManagementClass(scope, servicePath, null))
            {
                using (ManagementObject service = serviceClass.GetInstances().Cast<ManagementObject>().First())
                {
                    // Get the VM object
                    ObjectQuery vmQuery = new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName}'");
                    using (ManagementObjectSearcher vmSearcher = new ManagementObjectSearcher(scope, vmQuery))
                    {
                        ManagementObject vm = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                        if (vm == null)
                        {
                            throw new Exception($"VM '{vmName}' not found.");
                        }

                        // Get the VM's virtual system setting data (TargetSystem)
                        using (ManagementObjectCollection settings = vm.GetRelated("Msvm_VirtualSystemSettingData"))
                        {
                            ManagementObject setting = settings.Cast<ManagementObject>().FirstOrDefault();
                            if (setting == null)
                            {
                                throw new Exception("Unable to retrieve VM setting data.");
                            }
                            string target = setting.Path.Path;

                            // Create the KVP item instance
                            ManagementPath kvpPath = new ManagementPath("Msvm_KvpExchangeDataItem");
                            using (ManagementClass kvpClass = new ManagementClass(scope, kvpPath, null))
                            {
                                using (ManagementObject kvpItem = kvpClass.CreateInstance())
                                {
                                    kvpItem["Data"] = value;
                                    kvpItem["Name"] = key;  // 'Name' is the key property
                                    kvpItem["Source"] = 0;  // 0 indicates host-origin

                                    // Prepare parameters for AddKvpItems
                                    ManagementBaseObject inParams = service.GetMethodParameters("AddKvpItems");
                                    inParams["TargetSystem"] = target;
                                    inParams["DataItems"] = new ManagementBaseObject[] { kvpItem };

                                    // Invoke the method
                                    ManagementBaseObject outParams = service.InvokeMethod("AddKvpItems", inParams, null);
                                    uint returnValue = (uint)outParams["ReturnValue"];
                                    if (returnValue != 0)
                                    {
                                        throw new Exception($"Failed to add KVP: Return code {returnValue}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}