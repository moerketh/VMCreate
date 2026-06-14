using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Management.Automation;

namespace VMCreate
{
    /// <summary>
    /// Self-contained, elevated-only injection logic that writes the bundled
    /// <c>Unattend/unattend.xml</c> into a Windows VHDX via <c>Mount-VHD</c>.
    /// This class is meant to run inside a one-shot elevated child process
    /// (triggered by <see cref="ElevatedUnattendInjector"/>). It owns its own
    /// PowerShell runspace and logger — no DI container needed.
    /// </summary>
    public class UnattendInjector : IDisposable
    {
        private readonly PowerShell _ps;
        private readonly ILogger _logger;

        public UnattendInjector(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ps = PowerShell.Create();
            _ps.AddCommand("Import-Module").AddParameter("Name", "Hyper-V").Invoke();
            if (_ps.HadErrors)
            {
                string error = string.Join("; ", _ps.Streams.Error.Select(e => e.ToString()));
                throw new Exception($"Failed to import Hyper-V module: {error}");
            }
            _ps.Commands.Clear();
        }

        /// <summary>
        /// Mounts the VHDX, injects <c>unattend.xml</c> into the Windows
        /// Panther / Panther\Unattend / Sysprep directories (clearing any
        /// cached answer files first), then dismounts.
        /// Requires a full-Administrator token (Mount-VHD).
        /// </summary>
        /// <param name="vhdxPath">Path to the VHDX file.</param>
        public void Inject(string vhdxPath)
        {
            if (string.IsNullOrWhiteSpace(vhdxPath))
                throw new ArgumentNullException(nameof(vhdxPath));
            if (!File.Exists(vhdxPath))
                throw new FileNotFoundException("VHDX not found", vhdxPath);

            string unattendContent = File.ReadAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Unattend", "unattend.xml"));

            _logger.LogInformation("Injecting unattend.xml into VHDX: {VhdxPath}", vhdxPath);

            // ── Idempotent cleanup: dismount if a previous failed run left the VHDX mounted ──
            _ps.Commands.Clear();
            _ps.Streams.Error.Clear();
            _ps.AddCommand("Dismount-VHD").AddParameter("Path", vhdxPath);
            _ps.Invoke();
            if (!_ps.HadErrors)
                _logger.LogInformation("Dismounted leftover VHDX mount before injection: {VhdxPath}", vhdxPath);
            // Errors are expected if the VHDX wasn't mounted — ignore them.

            // ── Mount the VHDX without a drive letter ──────────────────────
            _ps.Commands.Clear();
            _ps.Streams.Error.Clear();
            _ps.AddCommand("Mount-VHD")
                .AddParameter("Path", vhdxPath)
                .AddParameter("ReadOnly", false)
                .AddParameter("NoDriveLetter", true);
            _ps.Invoke();
            if (_ps.HadErrors)
                throw new Exception($"Mount-VHD failed: {string.Join("; ", _ps.Streams.Error.Select(e => e.ToString()))}");

            // Get the disk number for the mounted VHDX
            _ps.Commands.Clear();
            _ps.Streams.Error.Clear();
            _ps.AddCommand("Get-VHD").AddParameter("Path", vhdxPath);
            var vhdResult = _ps.Invoke();
            if (_ps.HadErrors || vhdResult.Count == 0)
                throw new Exception($"Get-VHD failed after mount: {string.Join("; ", _ps.Streams.Error.Select(e => e.ToString()))}");
            int diskNumber = Convert.ToInt32(vhdResult[0].Properties["DiskNumber"].Value);
            _logger.LogInformation("Mounted VHDX as disk {DiskNumber}", diskNumber);

            string mountFolder = null;
            try
            {
                // ── Find the Windows partition and assign a mount folder ────
                _ps.Commands.Clear();
                _ps.Streams.Error.Clear();
                _ps.AddCommand("Get-Partition")
                    .AddParameter("DiskNumber", diskNumber);
                var partitions = _ps.Invoke();
                if (_ps.HadErrors)
                    throw new Exception($"Get-Partition failed: {string.Join("; ", _ps.Streams.Error.Select(e => e.ToString()))}");

                mountFolder = Path.Combine(Path.GetTempPath(), "VMCreate-Inject-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(mountFolder);

                bool injected = false;
                foreach (var partition in partitions)
                {
                    string driveLetter = partition.Properties["DriveLetter"]?.Value?.ToString();
                    string gptType = partition.Properties["GptType"]?.Value?.ToString();
                    var typeProp = partition.Properties["Type"]?.Value;
                    string typeStr = typeProp?.ToString() ?? "";

                    // Skip EFI (C12A7328-F81F-11D2-BA4B-00A0C93EC93B) and MSR partitions
                    bool isEfi = string.Equals(gptType, "C12A7328-F81F-11D2-BA4B-00A0C93EC93B", StringComparison.OrdinalIgnoreCase);
                    bool isMsr = string.Equals(gptType, "E3C9E316-0B5C-4DB8-817D-F92DF00215AE", StringComparison.OrdinalIgnoreCase);
                    if (isEfi || isMsr)
                        continue;

                    // Try to mount this partition and look for Windows
                    string partitionMount = mountFolder + "\\" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    Directory.CreateDirectory(partitionMount);

                    try
                    {
                        _ps.Commands.Clear();
                        _ps.Streams.Error.Clear();
                        _ps.AddCommand("Add-PartitionAccessPath")
                            .AddParameter("DiskNumber", diskNumber)
                            .AddParameter("PartitionNumber", Convert.ToInt32(partition.Properties["PartitionNumber"].Value))
                            .AddParameter("AccessPath", partitionMount);
                        _ps.Invoke();
                        if (_ps.HadErrors)
                        {
                            _logger.LogDebug("Could not mount partition {PartitionNumber}: {Error}",
                                partition.Properties["PartitionNumber"].Value,
                                string.Join("; ", _ps.Streams.Error.Select(e => e.ToString())));
                            continue;
                        }

                        // Check if this is the Windows partition
                        string windowsDir = Path.Combine(partitionMount, "Windows");
                        if (!Directory.Exists(windowsDir))
                        {
                            // Not the Windows partition — unmount and skip
                            RemovePartitionAccessPath(diskNumber, Convert.ToInt32(partition.Properties["PartitionNumber"].Value), partitionMount);
                            continue;
                        }

                        _logger.LogInformation("Found Windows partition at mount {Mount}", partitionMount);

                        // ── Delete cached answer files ───────────────────────
                        // The Microsoft dev VHDX has cached answer files from
                        // its original sysprep in %WINDIR%\Panther\. These must
                        // be removed before injecting ours to prevent conflicts.
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

                        // Also check $Windows.~BT\Sources\Panther for cached files
                        // (independent of Panther dir existence)
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

                        // ── Offline Windows Defender disable (before first boot) ──
                        // Tamper Protection blocks all post-boot attempts to disable Defender.
                        // By modifying the offline SOFTWARE hive here (while the VHDX is mounted),
                        // Defender services start disabled on the very first boot — before
                        // WdFilter/WdBoot ever load. This is the only reliable method.
                        DisableWindowsDefenderOffline(partitionMount, windowsDir);

                        // ── Write unattend.xml to the three injection targets ─
                        // Priority 3: %WINDIR%\Panther\Unattend.xml (always searched)
                        string pantherUnattend = Path.Combine(pantherDir, "Unattend.xml");
                        File.WriteAllText(pantherUnattend, unattendContent);
                        _logger.LogInformation("Wrote unattend.xml to {Path}", pantherUnattend);

                        // Priority 2: %WINDIR%\Panther\Unattend\Unattend.xml (downlevel)
                        string pantherUnattendDir = Path.Combine(pantherDir, "Unattend");
                        Directory.CreateDirectory(pantherUnattendDir);
                        File.WriteAllText(Path.Combine(pantherUnattendDir, "Unattend.xml"), unattendContent);
                        _logger.LogInformation("Wrote unattend.xml to {Path}", Path.Combine(pantherUnattendDir, "Unattend.xml"));

                        // Priority 6: %WINDIR%\System32\Sysprep\Unattend.xml (oobeSystem pass)
                        string sysprepDir = Path.Combine(windowsDir, "System32", "Sysprep");
                        Directory.CreateDirectory(sysprepDir);
                        File.WriteAllText(Path.Combine(sysprepDir, "Unattend.xml"), unattendContent);
                        _logger.LogInformation("Wrote unattend.xml to {Path}", Path.Combine(sysprepDir, "Unattend.xml"));

                        injected = true;

                        // Unmount this partition
                        RemovePartitionAccessPath(diskNumber, Convert.ToInt32(partition.Properties["PartitionNumber"].Value), partitionMount);
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error processing partition, attempting to continue");
                        try { RemovePartitionAccessPath(diskNumber, Convert.ToInt32(partition.Properties["PartitionNumber"].Value), partitionMount); }
                        catch { /* best effort */ }
                    }
                }

                if (!injected)
                    throw new Exception("No Windows partition found in the VHDX. Cannot inject unattend.xml.");
            }
            finally
            {
                // ── Always dismount the VHDX ────────────────────────────────
                try
                {
                    _ps.Commands.Clear();
                    _ps.Streams.Error.Clear();
                    _ps.AddCommand("Dismount-VHD").AddParameter("Path", vhdxPath);
                    _ps.Invoke();
                    if (_ps.HadErrors)
                        _logger.LogError("Dismount-VHD failed: {Error}", string.Join("; ", _ps.Streams.Error.Select(e => e.ToString())));
                    else
                        _logger.LogInformation("Dismounted VHDX: {VhdxPath}", vhdxPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Dismount-VHD threw unexpectedly");
                }

                // ── Clean up temp mount folders ─────────────────────────────
                if (mountFolder != null)
                {
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
            }
        }

        /// <summary>
        /// Disables Windows Defender offline by loading the mounted VHDX's SOFTWARE
        /// registry hive and setting the required service and Defender keys. Because
        /// this runs before the VM ever boots, WdFilter/WdBoot are not yet active
        /// and cannot block the changes. After this, the VM's first boot sees Defender
        /// as already disabled.
        /// </summary>
        private void DisableWindowsDefenderOffline(string partitionMount, string windowsDir)
        {
            string softwareHivePath = Path.Combine(partitionMount, "Windows", "System32", "config", "SOFTWARE");
            string systemHivePath = Path.Combine(partitionMount, "Windows", "System32", "config", "SYSTEM");
            string offlineRegName = $"VMCreateOffline_{Guid.NewGuid():N}";
            string offlineSystemRegName = $"VMCreateOfflineSys_{Guid.NewGuid():N}";

            try
            {
                _logger.LogInformation("Disabling Windows Defender offline via registry hive editing...");

                // Load the offline SOFTWARE hive
                _ps.Commands.Clear();
                _ps.Streams.Error.Clear();
                _ps.AddCommand("reg").AddArgument("load")
                    .AddArgument($"HKLM\\{offlineRegName}")
                    .AddArgument(softwareHivePath);
                _ps.Invoke();
                if (_ps.HadErrors)
                {
                    _logger.LogError("Failed to load offline SOFTWARE hive: {Error}",
                        string.Join("; ", _ps.Streams.Error.Select(e => e.ToString())));
                    return;
                }

                // Load the offline SYSTEM hive (for service start types)
                _ps.Commands.Clear();
                _ps.Streams.Error.Clear();
                _ps.AddCommand("reg").AddArgument("load")
                    .AddArgument($"HKLM\\{offlineSystemRegName}")
                    .AddArgument(systemHivePath);
                _ps.Invoke();
                if (_ps.HadErrors)
                {
                    _logger.LogWarning("Failed to load offline SYSTEM hive: {Error}",
                        string.Join("; ", _ps.Streams.Error.Select(e => e.ToString())));
                    // Continue anyway — SOFTWARE hive changes are the most important
                }

                // ── SOFTWARE hive: Defender configuration ──
                string[] softwareKeys = new[]
                {
                    $"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender",
                    $"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Features",
                    $"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Real-Time Protection",
                    $"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Spynet",
                    $"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\UX Configuration",
                    $"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows Defender",
                    $"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection",
                    $"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows Defender\\Features",
                    $"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows\\WindowsUpdate",
                    $"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU",
                };

                foreach (var keyPath in softwareKeys)
                {
                    _ps.Commands.Clear();
                    _ps.Streams.Error.Clear();
                    _ps.AddCommand("reg").AddArgument("add")
                        .AddArgument(keyPath)
                        .AddArgument("/f");
                    _ps.Invoke();
                    // Ignore errors — key may already exist
                }

                // Disable Defender main features
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender", "DisableAntiVirus", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender", "DisableAntiSpyware", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Features", "TamperProtection", 0);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Features", "ForceTamperProtection", 0);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Features", "DisableAntiVirus", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Features", "DisableAntiSpyware", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableRealtimeMonitoring", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableBehaviorMonitoring", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableOnAccessProtection", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableIOAVProtection", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableScanOnRealtimeEnable", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Spynet", "SpyNetReporting", 0);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Microsoft\\Windows Defender\\Spynet", "SubmitSamplesConsent", 0);

                // Disable via Group Policy keys in offline registry
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows Defender", "DisableAntiSpyware", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableRealtimeMonitoring", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableBehaviorMonitoring", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", "DisableOnAccessProtection", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows Defender\\Features", "TamperProtection", 0);

                // ── Disable SmartScreen (offline) ──
                // SmartScreen can block malware analysis tools and downloaded payloads.
                // Disable it via Explorer policy and Windows System policy before first boot.
                SetOfflineRegString($"HKLM\\{offlineRegName}\\Microsoft\\Windows\\CurrentVersion\\Explorer", "SmartScreenEnabled", "Off");
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows\\System", "EnableSmartScreen", 0);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows\\System", "ShellSmartScreenLevel", 0);

                // ── Disable Windows Updates (offline) ──
                // Set Group Policy keys before first boot so Windows Update is blocked
                // even if the service Start=4 modification fails due to ACL restrictions.
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU", "NoAutoUpdate", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows\\WindowsUpdate", "DoNotConnectToWindowsUpdateInternetLocations", 1);
                SetOfflineRegDword($"HKLM\\{offlineRegName}\\Policies\\Microsoft\\Windows\\WindowsUpdate", "SetAutoDownloadMinor", 0);

                // ── SYSTEM hive: disable service start types ──
                string[] servicesToDisable = new[]
                {
                    "WinDefend", "WdBoot", "WdFilter", "WdNisDrv", "WdNisSvc",
                    "WdDevFlt", "WdKern", "WdKrn", "WdKrnProc",
                    "SecurityHealthService", "Sense", "SgrmBroker", "SgrmDeployment",
                    "wuauserv", "WaaSMedicSvc"
                };

                foreach (var svc in servicesToDisable)
                {
                    string svcKey = $"HKLM\\{offlineSystemRegName}\\ControlSet001\\Services\\{svc}";
                    _ps.Commands.Clear();
                    _ps.Streams.Error.Clear();
                    _ps.AddCommand("reg").AddArgument("add")
                        .AddArgument(svcKey)
                        .AddArgument("/v").AddArgument("Start")
                        .AddArgument("/t").AddArgument("REG_DWORD")
                        .AddArgument("/d").AddArgument("4")
                        .AddArgument("/f");
                    _ps.Invoke();
                    if (_ps.HadErrors)
                    {
                        _logger.LogDebug("Could not set Start=Disabled for service {Service}: {Error}",
                            svc, string.Join("; ", _ps.Streams.Error.Select(e => e.ToString())));
                    }
                    else
                    {
                        _logger.LogDebug("Set service {Service} Start=Disabled (4) in offline SYSTEM hive", svc);
                    }
                }

                // Also set ControlSet002 if it exists
                foreach (var svc in servicesToDisable)
                {
                    string svcKey = $"HKLM\\{offlineSystemRegName}\\ControlSet002\\Services\\{svc}";
                    _ps.Commands.Clear();
                    _ps.Streams.Error.Clear();
                    _ps.AddCommand("reg").AddArgument("add")
                        .AddArgument(svcKey)
                        .AddArgument("/v").AddArgument("Start")
                        .AddArgument("/t").AddArgument("REG_DWORD")
                        .AddArgument("/d").AddArgument("4")
                        .AddArgument("/f");
                    _ps.Invoke();
                    // Ignore errors — ControlSet002 may not exist
                }

                // Disable Windows Update service too (prevents Defender from being re-enabled)
                string[] wuServices = new[] { "wuauserv", "WaaSMedicSvc" };
                foreach (var svc in wuServices)
                {
                    foreach (var cs in new[] { "ControlSet001", "ControlSet002" })
                    {
                        string svcKey = $"HKLM\\{offlineSystemRegName}\\{cs}\\Services\\{svc}";
                        _ps.Commands.Clear();
                        _ps.Streams.Error.Clear();
                        _ps.AddCommand("reg").AddArgument("add")
                            .AddArgument(svcKey)
                            .AddArgument("/v").AddArgument("Start")
                            .AddArgument("/t").AddArgument("REG_DWORD")
                            .AddArgument("/d").AddArgument("4")
                            .AddArgument("/f");
                        _ps.Invoke();
                    }
                }

                _logger.LogInformation("Windows Defender offline disable completed successfully");
            }
            finally
            {
                // ── Unload hives ──
                try
                {
                    _ps.Commands.Clear();
                    _ps.Streams.Error.Clear();
                    _ps.AddCommand("reg").AddArgument("unload").AddArgument($"HKLM\\{offlineRegName}");
                    _ps.Invoke();
                }
                catch { /* best effort */ }

                try
                {
                    _ps.Commands.Clear();
                    _ps.Streams.Error.Clear();
                    _ps.AddCommand("reg").AddArgument("unload").AddArgument($"HKLM\\{offlineSystemRegName}");
                    _ps.Invoke();
                }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Helper to set a REG_SZ (string) value in the offline registry via reg.exe.
        /// </summary>
        private void SetOfflineRegString(string keyPath, string valueName, string value)
        {
            _ps.Commands.Clear();
            _ps.Streams.Error.Clear();
            _ps.AddCommand("reg").AddArgument("add")
                .AddArgument(keyPath)
                .AddArgument("/v").AddArgument(valueName)
                .AddArgument("/t").AddArgument("REG_SZ")
                .AddArgument("/d").AddArgument(value)
                .AddArgument("/f");
            _ps.Invoke();
            if (_ps.HadErrors)
            {
                _logger.LogDebug("Could not set registry value {Key}\\{Value}: {Error}",
                    keyPath, valueName, string.Join("; ", _ps.Streams.Error.Select(e => e.ToString())));
            }
            else
            {
                _logger.LogDebug("Set registry value {Key}\\{Value} = {Data}", keyPath, valueName, value);
            }
        }

        /// <summary>
        /// Helper to set a REG_DWORD value in the offline registry via reg.exe.
        /// </summary>
        private void SetOfflineRegDword(string keyPath, string valueName, int value)
        {
            _ps.Commands.Clear();
            _ps.Streams.Error.Clear();
            _ps.AddCommand("reg").AddArgument("add")
                .AddArgument(keyPath)
                .AddArgument("/v").AddArgument(valueName)
                .AddArgument("/t").AddArgument("REG_DWORD")
                .AddArgument("/d").AddArgument(value.ToString())
                .AddArgument("/f");
            _ps.Invoke();
            if (_ps.HadErrors)
            {
                _logger.LogDebug("Could not set registry value {Key}\\{Value}: {Error}",
                    keyPath, valueName, string.Join("; ", _ps.Streams.Error.Select(e => e.ToString())));
            }
            else
            {
                _logger.LogDebug("Set registry value {Key}\\{Value} = {Data}", keyPath, valueName, value);
            }
        }

        public void Dispose()
        {
            _ps?.Dispose();
        }

        private void RemovePartitionAccessPath(int diskNumber, int partitionNumber, string accessPath)
        {
            _ps.Commands.Clear();
            _ps.Streams.Error.Clear();
            _ps.AddCommand("Remove-PartitionAccessPath")
                .AddParameter("DiskNumber", diskNumber)
                .AddParameter("PartitionNumber", partitionNumber)
                .AddParameter("AccessPath", accessPath);
            _ps.Invoke();
            if (_ps.HadErrors)
                _logger.LogWarning("Remove-PartitionAccessPath failed: {Error}",
                    string.Join("; ", _ps.Streams.Error.Select(e => e.ToString())));
        }
    }
}