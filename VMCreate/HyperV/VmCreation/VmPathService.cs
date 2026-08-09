using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Security.Principal;

namespace VMCreate.HyperV.VmCreation
{
    public class VmPathService : IVmPathService
    {
        private readonly ILogger<VmPathService> _logger;

        public VmPathService(ILogger<VmPathService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            DefaultVmPath = ResolveDefaultVmPath();
            DefaultVhdxPath = ResolveDefaultVhdxPath();
        }

        public string DefaultVmPath { get; }
        public string DefaultVhdxPath { get; }

        public string GetVirtualHardDiskPath(string vmName)
        {
            if (string.IsNullOrWhiteSpace(vmName))
                throw new ArgumentException("VM name is required.", nameof(vmName));
            return Path.Combine(DefaultVhdxPath, vmName);
        }

        private string ResolveDefaultVmPath()
        {
            string[] defaultPaths = new[]
            {
                @"C:\ProgramData\Microsoft\Windows\Hyper-V",
                @"C:\Users\Public\Documents\Hyper-V\Virtual Machines"
            };

            string[] registryPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization",
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtual Machine Manager"
            };

            string[] valueNames = new[]
            { "DefaultExternalDataRoot", "DefaultVirtualMachinePath", "VirtualMachinePath" };

            foreach (string regPath in registryPaths)
            {
                try
                {
                    using RegistryKey key = Registry.LocalMachine.OpenSubKey(regPath);
                    if (key == null)
                    {
                        _logger.LogDebug("Registry key not found: {Key}", regPath);
                        continue;
                    }

                    foreach (string valName in valueNames)
                    {
                        object rawValue = key.GetValue(valName);
                        if (rawValue == null)
                        {
                            _logger.LogDebug("Value {ValueName} not found under {Key}", valName, regPath);
                            continue;
                        }

                        string path = rawValue.ToString();
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            _logger.LogDebug("Value {ValueName} is empty under {Key}", valName, regPath);
                            continue;
                        }

                        path = path.TrimEnd('\\', '/');

                        if (Directory.Exists(path))
                        {
                            _logger.LogInformation("Using VM path from registry [{Key} > {ValueName}]: {Path}", regPath, valName, path);
                            return path;
                        }
                        else
                        {
                            _logger.LogWarning("Registry path does not exist: {Path} (from {Key} > {ValueName})", path, regPath, valName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading registry key {Key}: {Message}", regPath, ex.Message);
                }
            }

            foreach (string fallback in defaultPaths)
            {
                if (Directory.Exists(fallback))
                {
                    _logger.LogInformation("Using default VM path: {Path}", fallback);
                    return fallback;
                }
            }

            _logger.LogError("No valid VM path found in registry or default locations. Using last fallback: {Path}", defaultPaths[0]);
            return defaultPaths[0];
        }

        /// <summary>
        /// Well-known SID for the local "Hyper-V Administrators" group.
        /// </summary>
        private static readonly SecurityIdentifier HyperVAdministratorsSid =
            new SecurityIdentifier("S-1-5-32-578");

        public bool IsHyperVAdministrator()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(HyperVAdministratorsSid);
        }

        private string ResolveDefaultVhdxPath()
        {
            // Fall back to a user-writable location so that a non-elevated Hyper-V
            // Administrator can extract/convert disks without needing write access
            // to the ACL-protected C:\ProgramData\...\Virtual Hard Disks folder.
            // If the registry value is set (by Hyper-V Manager or the user), it is
            // respected — the fallback only applies when the registry key is absent.
            string defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Hyper-V", "Virtual Hard Disks");
            try
            {
                string registryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
                using RegistryKey key = Registry.LocalMachine.OpenSubKey(registryPath);
                if (key != null)
                {
                    string path = key.GetValue("DefaultVirtualHardDiskPath") as string;
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        _logger.LogInformation("Using DefaultVirtualHardDiskPath from registry: {Path}", path);
                        return path;
                    }
                }
                _logger.LogInformation("DefaultVirtualHardDiskPath not found or invalid. Using user-writable default: {DefaultPath}", defaultPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading DefaultVirtualHardDiskPath: {Message}", ex.Message);
            }
            return defaultPath;
        }
    }
}
