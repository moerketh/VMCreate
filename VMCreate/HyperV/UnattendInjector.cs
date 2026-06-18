using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV.Unattend;

namespace VMCreate
{
    /// <summary>
    /// Injects the bundled unattend.xml into a Windows VHDX. DI-friendly implementation
    /// that delegates PowerShell execution to <see cref="IPowerShellExecutor"/...gt; and
    /// offline registry work to <see cref="IOfflineRegistryEditor"/...gt;.
    /// </summary>
    public sealed class UnattendInjector : IUnattendInjector
    {
        private readonly IPowerShellExecutor _powerShell;
        private readonly IOfflineRegistryEditor _registry;
        private readonly ILogger<UnattendInjector> _logger;

        public UnattendInjector(IPowerShellExecutor powerShell, IOfflineRegistryEditor registry, ILogger<UnattendInjector> logger)
        {
            _powerShell = powerShell ?? throw new ArgumentNullException(nameof(powerShell));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> InjectAsync(string vhdxPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(vhdxPath))
                throw new ArgumentNullException(nameof(vhdxPath));
            if (!File.Exists(vhdxPath))
                throw new FileNotFoundException("VHDX not found", vhdxPath);

            // Synchronous Mount-VHD/IO work; run it on the default scheduler.
            return await Task.Run(() => InjectCore(vhdxPath), cancellationToken);
        }

        private bool InjectCore(string vhdxPath)
        {
            string unattendContent = File.ReadAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Unattend", "unattend.xml"));

            _logger.LogInformation("Injecting unattend.xml into VHDX: {VhdxPath}", vhdxPath);

            Dismount(vhdxPath);
            Mount(vhdxPath);

            int diskNumber = GetDiskNumber(vhdxPath);
            string mountFolder = Path.Combine(Path.GetTempPath(), "VMCreate-Inject-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(mountFolder);

            bool injected = false;
            try
            {
                var partitionResult = _powerShell.RunCommand("Get-Partition", ("DiskNumber", diskNumber));
                if (partitionResult.HadErrors)
                    throw new InvalidOperationException($"Get-Partition failed: {partitionResult.ErrorSummary}");

                foreach (var partition in partitionResult.Output)
                {
                    string gptType = partition.Properties["GptType"]?.Value?.ToString();
                    if (string.Equals(gptType, "C12A7328-F81F-11D2-BA4B-00A0C93EC93B", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(gptType, "E3C9E316-0B5C-4DB8-817D-F92DF00215AE", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string partitionMount = Path.Combine(mountFolder, Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(partitionMount);

                    try
                    {
                        int partitionNumber = Convert.ToInt32(partition.Properties["PartitionNumber"].Value);
                        AddPartitionAccessPath(diskNumber, partitionNumber, partitionMount);

                        string windowsDir = Path.Combine(partitionMount, "Windows");
                        if (!Directory.Exists(windowsDir))
                        {
                            RemovePartitionAccessPath(diskNumber, partitionNumber, partitionMount);
                            continue;
                        }

                        _logger.LogInformation("Found Windows partition at mount {Mount}", partitionMount);
                        InjectIntoWindowsPartition(partitionMount, windowsDir, unattendContent);
                        injected = true;
                        RemovePartitionAccessPath(diskNumber, partitionNumber, partitionMount);
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error processing partition, attempting to continue");
                        try
                        {
                            int partitionNumber = Convert.ToInt32(partition.Properties["PartitionNumber"].Value);
                            RemovePartitionAccessPath(diskNumber, partitionNumber, partitionMount);
                        }
                        catch { }
                    }
                }

                if (!injected)
                    throw new InvalidOperationException("No Windows partition found in the VHDX. Cannot inject unattend.xml.");
            }
            finally
            {
                Dismount(vhdxPath);
                try
                {
                    if (Directory.Exists(mountFolder))
                        Directory.Delete(mountFolder, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not delete temp mount folder {Path} (non-fatal)", mountFolder);
                }
            }

            _logger.LogInformation("Unattend injection completed for {VhdxPath}", vhdxPath);
            return true;
        }

        private void InjectIntoWindowsPartition(string partitionMount, string windowsDir, string unattendContent)
        {
            string pantherDir = Path.Combine(windowsDir, "Panther");
            if (Directory.Exists(pantherDir))
            {
                foreach (string cached in Directory.GetFiles(pantherDir, "*.xml"))
                {
                    try
                    {
                        File.Delete(cached);
                        _logger.LogDebug("Deleted cached answer file: {File}", cached);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete cached answer file: {File}", cached);
                    }
                }
            }

            string btPanther = Path.Combine(partitionMount, "$Windows.~BT", "Sources", "Panther");
            if (Directory.Exists(btPanther))
            {
                foreach (string cached in Directory.GetFiles(btPanther, "*.xml"))
                {
                    try
                    {
                        File.Delete(cached);
                        _logger.LogDebug("Deleted cached BT answer file: {File}", cached);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete cached BT answer file: {File}", cached);
                    }
                }
            }

            DisableWindowsDefender(partitionMount, windowsDir);

            Directory.CreateDirectory(pantherDir);
            File.WriteAllText(Path.Combine(pantherDir, "Unattend.xml"), unattendContent);
            File.WriteAllText(Path.Combine(pantherDir, "Unattend", "Unattend.xml"), unattendContent);
            File.WriteAllText(Path.Combine(windowsDir, "System32", "Sysprep", "Unattend.xml"), unattendContent);
        }

        private void DisableWindowsDefender(string partitionMount, string windowsDir)
        {
            string softwareHive = Path.Combine(partitionMount, "Windows", "System32", "config", "SOFTWARE");
            string systemHive = Path.Combine(partitionMount, "Windows", "System32", "config", "SYSTEM");
            if (!File.Exists(softwareHive))
            {
                _logger.LogWarning("SOFTWARE hive not found at {Path}; skipping Defender disable", softwareHive);
                return;
            }

            string softwareName = $"VMCreateOffline_{Guid.NewGuid():N}";
            string systemName = $"VMCreateOfflineSys_{Guid.NewGuid():N}";

            try
            {
                _registry.LoadHive(softwareHive, softwareName);
                try { _registry.LoadHive(systemHive, systemName); } catch { /* optional */ }

                DisableDefenderInSoftwareHive(softwareName);
                DisableDefenderInSystemHive(systemName);
            }
            finally
            {
                _registry.UnloadHive(softwareName);
                _registry.UnloadHive(systemName);
            }
        }

        private void DisableDefenderInSoftwareHive(string mountName)
        {
            string[] keys = new[]
            {
                $"HKLM\\{mountName}\\Microsoft\\Windows Defender",
                $"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Features",
                $"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Real-Time Protection",
                $"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Spynet",
                $"HKLM\\{mountName}\\Microsoft\\Windows Defender\\UX Configuration",
                $"HKLM\\{mountName}\\Policies\\Microsoft\\Windows Defender",
                $"HKLM\\{mountName}\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection",
                $"HKLM\\{mountName}\\Policies\\Microsoft\\Windows Defender\\Features",
                $"HKLM\\{mountName}\\Policies\\Microsoft\\Windows\\WindowsUpdate",
                $"HKLM\\{mountName}\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU"
            };

            foreach (var key in keys)
                _registry.AddKey(key);

            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender", "DisableAntiVirus", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender", "DisableAntiSpyware", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Features", "TamperProtection", 0);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Features", "ForceTamperProtection", 0);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Features", "DisableAntiVirus", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Features", "DisableAntiSpyware", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableRealtimeMonitoring", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableBehaviorMonitoring", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableOnAccessProtection", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableIOAVProtection", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableScanOnRealtimeEnable", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Spynet", "SpyNetReporting", 0);
            _registry.SetDword($"HKLM\\{mountName}\\Microsoft\\Windows Defender\\Spynet", "SubmitSamplesConsent", 0);
            _registry.SetDword($"HKLM\\{mountName}\\Policies\\Microsoft\\Windows Defender", "DisableAntiSpyware", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableRealtimeMonitoring", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableBehaviorMonitoring", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableOnAccessProtection", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Policies\\Microsoft\\Windows Defender\\Features", "TamperProtection", 0);
            _registry.SetString($"HKLM\\{mountName}\\Microsoft\\Windows\\CurrentVersion\\Explorer", "SmartScreenEnabled", "Off");
            _registry.SetDword($"HKLM\\{mountName}\\Policies\\Microsoft\\Windows\\System", "EnableSmartScreen", 0);
            _registry.SetDword($"HKLM\\{mountName}\\Policies\\Microsoft\\Windows\\System", "ShellSmartScreenLevel", 0);
            _registry.SetDword($"HKLM\\{mountName}\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU", "NoAutoUpdate", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Policies\\Microsoft\\Windows\\WindowsUpdate", "DoNotConnectToWindowsUpdateInternetLocations", 1);
            _registry.SetDword($"HKLM\\{mountName}\\Policies\\Microsoft\\Windows\\WindowsUpdate", "SetAutoDownloadMinor", 0);
        }

        private void DisableDefenderInSystemHive(string mountName)
        {
            string[] services = new[]
            {
                "WinDefend", "WdBoot", "WdFilter", "WdNisDrv", "WdNisSvc",
                "WdDevFlt", "WdKern", "WdKrn", "WdKrnProc",
                "SecurityHealthService", "Sense", "SgrmBroker", "SgrmDeployment",
                "wuauserv", "WaaSMedicSvc"
            };

            foreach (var cs in new[] { "ControlSet001", "ControlSet002" })
            {
                foreach (var svc in services)
                {
                    try
                    {
                        _registry.SetServiceStart(mountName, cs, svc, 4);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Could not set Start for {Service} in {ControlSet}: {Message}", svc, cs, ex.Message);
                    }
                }
            }
        }

        private void Mount(string vhdxPath)
        {
            var result = _powerShell.RunCommand("Mount-VHD",
                ("Path", vhdxPath),
                ("ReadOnly", false),
                ("NoDriveLetter", true));
            if (result.HadErrors)
                throw new InvalidOperationException($"Mount-VHD failed: {result.ErrorSummary}");
        }

        private void Dismount(string vhdxPath)
        {
            var result = _powerShell.RunCommand("Dismount-VHD", ("Path", vhdxPath));
            if (!result.HadErrors)
                _logger.LogInformation("Dismounted leftover VHDX mount before injection: {VhdxPath}", vhdxPath);
        }

        private int GetDiskNumber(string vhdxPath)
        {
            var result = _powerShell.RunCommand("Get-VHD", ("Path", vhdxPath));
            if (result.HadErrors || result.Output.Count == 0)
                throw new InvalidOperationException($"Get-VHD failed after mount: {result.ErrorSummary}");
            return Convert.ToInt32(result.Output[0].Properties["DiskNumber"].Value);
        }

        private void AddPartitionAccessPath(int diskNumber, int partitionNumber, string accessPath)
        {
            var result = _powerShell.RunCommand("Add-PartitionAccessPath",
                ("DiskNumber", diskNumber),
                ("PartitionNumber", partitionNumber),
                ("AccessPath", accessPath));
            if (result.HadErrors)
                throw new InvalidOperationException($"Add-PartitionAccessPath failed: {result.ErrorSummary}");
        }

        private void RemovePartitionAccessPath(int diskNumber, int partitionNumber, string accessPath)
        {
            var result = _powerShell.RunCommand("Remove-PartitionAccessPath",
                ("DiskNumber", diskNumber),
                ("PartitionNumber", partitionNumber),
                ("AccessPath", accessPath));
            if (result.HadErrors)
                _logger.LogWarning("Remove-PartitionAccessPath failed: {Error}", result.ErrorSummary);
        }
    }
}
