using CreateVM;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Composite interface that inherits all role-specific Hyper-V manager interfaces.
    /// Consumers should prefer depending on the narrower role interfaces
    /// (IVmLifecycleManager, IVmDiskManager, etc.) whenever possible.
    /// </summary>
    public interface IHyperVManager : IVmLifecycleManager, IVmDiskManager, IVmBootManager, IVmNetworkManager, IVmConfigManager
    {
    }

    internal class PowerShellHyperVManager : IHyperVManager
    {
        private readonly ILogger<PowerShellHyperVManager> _logger;
        private readonly InitialSessionState _initialSessionState;

        public PowerShellHyperVManager(ILogger<PowerShellHyperVManager> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _initialSessionState = InitialSessionState.CreateDefault();
            // Import the Hyper-V module once per manager instance so every
            // PowerShell runspace created from this state has it available.
            _initialSessionState.ImportPSModule(new[] { "Hyper-V" });
        }

        /// <summary>
        /// Creates a fresh PowerShell instance backed by a dedicated runspace.
        /// Each public operation must use its own instance to avoid races when
        /// multiple async calls interleave command construction and invocation.
        /// </summary>
        private PowerShell CreatePowerShell()
        {
            var runspace = RunspaceFactory.CreateRunspace(_initialSessionState);
            runspace.Open();
            var ps = PowerShell.Create();
            ps.Runspace = runspace;
            return ps;
        }

        private static async Task<System.Collections.ObjectModel.Collection<PSObject>> RunCommand(
            PowerShell ps, CancellationToken cancellationToken)
        {
            ps.Streams.Error.Clear();
            var result = await Task.Run(ps.Invoke, cancellationToken);
            if (ps.HadErrors) throw new Exception(string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
            return result;
        }

        public async Task CreateVMAsync(VmSettings vmSettings, string vmPath, int targetGeneration, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();

            long memBytes = vmSettings.MemoryInMB * 1024L * 1024L;
            _logger.LogInformation(
                "Invoking New-VM -Name '{Name}' -Path '{Path}' -Generation {Gen} -MemoryStartupBytes {Mem} -NoVHD",
                vmSettings.VMName, vmPath, targetGeneration, memBytes);

            ps.AddCommand("New-VM")
                .AddParameter("Name", vmSettings.VMName)
                .AddParameter("MemoryStartupBytes", memBytes)
                .AddParameter("Path", vmPath)
                .AddParameter("Generation", targetGeneration)
                .AddParameter("NoVHD", true);

            var results = await Task.Run(() => ps.Invoke(), cancellationToken);
            _logger.LogInformation("New-VM invoked for {VMName}: {ResultCount} result(s), HadErrors={HadErrors}",
                vmSettings.VMName, results.Count, ps.HadErrors);

            if (ps.HadErrors)
            {
                var errors = ps.Streams.Error.Select(e =>
                    $"[Category={e.CategoryInfo?.Category}, Reason={e.CategoryInfo?.Reason}, " +
                    $"Id={e.FullyQualifiedErrorId}, Target={e.TargetObject}, Message={e.Exception?.Message}]");
                string errorDetail = string.Join("; ", errors);
                _logger.LogError("New-VM returned errors for {VMName}: {Errors}", vmSettings.VMName, errorDetail);

                // Check if the VM was actually created despite the error stream
                ps.Commands.Clear();
                ps.AddCommand("Get-VM").AddParameter("Name", vmSettings.VMName);
                var existing = await Task.Run(() => ps.Invoke(), cancellationToken);
                bool vmExists = existing.Count > 0 && !ps.HadErrors;
                _logger.LogInformation("Post-New-VM existence check for {VMName}: exists={Exists}", vmSettings.VMName, vmExists);

                if (vmExists)
                {
                    _logger.LogWarning("VM {VMName} exists despite New-VM error stream — continuing with warning", vmSettings.VMName);
                }
                else
                {
                    throw new Exception($"Failed to create a new virtual machine: {errorDetail}");
                }
            }

            // Disable automatic checkpoints — they create AVHDX differencing disks on
            // every VM start, which breaks SetFirstBootToHardDrive (firmware can't boot
            // from the transient AVHDX path).
            ps.Commands.Clear();
            ps.AddCommand("Set-VM")
                .AddParameter("Name", vmSettings.VMName)
                .AddParameter("AutomaticCheckpointsEnabled", false);
            await RunCommand(ps, cancellationToken);
        }

        public async Task SetVMLoginNotes(VmSettings vmSettings, string initialUsername, string initialPassword, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Set-VM")
                .AddParameter("Name", vmSettings.VMName)
                .AddParameter("Notes", $"Initial Username: {initialUsername}\r\nInitial Password: {initialPassword}");
            await RunCommand(ps, cancellationToken);

        }

        public async Task DisableDynamicMemory(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Set-VMMemory")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("DynamicMemoryEnabled", false);
            await RunCommand(ps, cancellationToken);
        }

        public async Task AddNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Add-VMNetworkAdapter")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Name", "Network Adapter");
            await RunCommand(ps, cancellationToken);
        }

        public async Task ConnectNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Connect-VMNetworkAdapter")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("SwitchName", "Default Switch");
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation("Connected VM to Default Switch for internet access.");
        }

        public async Task AddTemporaryNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Add-VMNetworkAdapter")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Name", "VMCreate Temp")
                .AddParameter("SwitchName", "Default Switch");
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation("Added temporary network adapter 'VMCreate Temp' on Default Switch.");
        }

        public async Task RemoveTemporaryNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddScript($@"
                $adapter = Get-VMNetworkAdapter -VMName '{vmSettings.VMName.Replace("'", "''")}' -Name 'VMCreate Temp' -ErrorAction SilentlyContinue
                if ($adapter) {{ Remove-VMNetworkAdapter -VMNetworkAdapter $adapter }}
            ");
            await Task.Run(ps.Invoke, cancellationToken);
            // Intentionally ignores errors — this is called idempotently when
            // the adapter may not exist yet (e.g. cleanup from a previous failed run).
            ps.Streams.Error.Clear();
            _logger.LogInformation("Removed temporary network adapter 'VMCreate Temp' (if present).");
        }

        /// <summary>
        /// Builds the path for the new boot drive created during MBR-to-GPT cloning.
        /// The path must NOT collide with the converted source VHDX (<c>{vmName}.vhdx</c>).
        /// </summary>
        internal static string GetNewDrivePath(string vmPath, string vmName)
            => Path.Combine(vmPath, $"{vmName}_boot.vhdx");

        public async Task AddNewHardDrive(VmSettings vmSettings, string vmPath, CancellationToken cancellationToken)
        {
            // Create new dynamic VHDX (suffixed to avoid collision with the converted source disk)
            string newVhdPath = GetNewDrivePath(vmPath, vmSettings.VMName);
            using var ps = CreatePowerShell();
            ps.AddCommand("New-VHD")
                .AddParameter("Path", newVhdPath)
                .AddParameter("Dynamic", true)
                .AddParameter("SizeBytes", vmSettings.NewDriveSizeInGB * 1024L * 1024L * 1024L);
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation($"Created new dynamic VHDX for cloning: {newVhdPath}");

            // Attach new VHDX
            ps.Commands.Clear();
            ps.AddCommand("Add-VMHardDiskDrive")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Path", newVhdPath)
                .AddParameter("ControllerType", "SCSI");
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation($"Attached new dynamic VHDX for cloning: {newVhdPath}");
        }

        public async Task AddExistingHardDrive(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Add-VMHardDiskDrive")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Path", mediaPath)
                .AddParameter("ControllerType", "SCSI");
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation($"Attached VHDX: {mediaPath}");
        }

        public async Task RemoveHardDrive(VmSettings vmSettings, int location, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Get-VMHardDiskDrive")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("ControllerType", "SCSI")
                .AddParameter("ControllerNumber", "0")
                .AddParameter("ControllerLocation", location);
            var drives = await RunCommand(ps, cancellationToken);
            if (drives.Count == 0)
            {
                _logger.LogWarning("No hard drive found at SCSI(0,{Location}) for VM {VMName} — nothing to remove.", location, vmSettings.VMName);
                return;
            }
            ps.Commands.Clear();
            ps.AddCommand("Remove-VMHardDiskDrive")
                .AddParameter("VMHardDiskDrive", drives[0]);
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation("Detached disk at SCSI(0,{Location}) for VM {VMName}", location, vmSettings.VMName);
        }

        public async Task AddBootDvd(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Checking for DVD drive on VM: {vmSettings.VMName}");
            using var ps = CreatePowerShell();
            ps.AddCommand("Get-VMDvdDrive")
                .AddParameter("VMName", vmSettings.VMName);
            var results = await RunCommand(ps, cancellationToken);

            if (results.Count == 0)
            {
                _logger.LogInformation($"No DVD drive found, adding one to VM: {vmSettings.VMName}");
                ps.Commands.Clear();
                ps.AddCommand("Add-VMDvdDrive")
                    .AddParameter("VMName", vmSettings.VMName);
                await RunCommand(ps, cancellationToken);
                _logger.LogInformation($"Added DVD drive to VM: {vmSettings.VMName}");
            }
            else
            {
                _logger.LogInformation($"DVD drive already exists on VM: {vmSettings.VMName}");
            }

            _logger.LogInformation("Attaching ISO as DVD drive: {MediaPath}", mediaPath);
            ps.Commands.Clear();
            ps.AddCommand("Set-VMDvdDrive")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Path", mediaPath);
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation("Attached ISO to DVD drive: {MediaPath}", mediaPath);
        }

        public async Task RemoveBootDvd(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Detaching ISO from DVD drive: {MediaPath}", mediaPath);
            using var ps = CreatePowerShell();
            ps.AddCommand("Set-VMDvdDrive")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Path", null);
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation("Detached ISO from DVD drive: {MediaPath}", mediaPath);
        }

        public async Task SetFirstBootToDvd(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Get-VMDvdDrive")
                .AddParameter("VMName", vmSettings.VMName);
            var dvdDrive = (await RunCommand(ps, cancellationToken)).FirstOrDefault();
            if (dvdDrive == null)
            {
                throw new Exception("No DVD drive found for VM. Ensure the cloning ISO is attached.");
            }
            ps.Commands.Clear();
            ps.AddCommand("Set-VMFirmware")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("FirstBootDevice", dvdDrive);
            await RunCommand(ps, cancellationToken);
        }

        public async Task SetFirstBootToHardDrive(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Get-VMHardDiskDrive")
                .AddParameter("VMName", vmSettings.VMName);
            var hardDrives = await RunCommand(ps, cancellationToken);
            var firstDrive = hardDrives.FirstOrDefault();
            if (firstDrive == null)
            {
                throw new Exception("No hard disk drive found for VM. Ensure a VHDX is attached.");
            }
            ps.Commands.Clear();
            ps.AddCommand("Set-VMFirmware")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("FirstBootDevice", firstDrive);
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation("Set first boot device to hard drive for VM: {VMName}", vmSettings.VMName);
        }

        public async Task SetCpuCount(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Set-VMProcessor")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Count", vmSettings.CPUCount);
            await RunCommand(ps, cancellationToken);
        }

        public async Task SetEnhancedSession(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            string transportType = string.IsNullOrEmpty(vmSettings.EnhancedSessionTransportType)
                ? "HvSocket"
                : vmSettings.EnhancedSessionTransportType;

            using var ps = CreatePowerShell();
            ps.AddCommand("Set-VM")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("EnhancedSessionTransportType", transportType);
            await RunCommand(ps, cancellationToken);
        }

        public async Task SetSecureBoot(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Set-VMFirmware")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("EnableSecureBoot", vmSettings.SecureBoot.ToOnOff())
                .AddParameter("SecureBootTemplate", vmSettings.SecureBootTemplate);
            await RunCommand(ps, cancellationToken);
        }

        public async Task StartVM(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Start-VM")
                .AddParameter("VMName", vmSettings.VMName);
            await RunCommand(ps, cancellationToken);
        }

        public async Task StartVMConnect(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            string escapedVmName = vmSettings.VMName.Replace("'", "''");
            string vmConnectCommand = $"& \"C:\\Windows\\System32\\vmconnect.exe\" localhost \"{escapedVmName}\"";
            _logger.LogDebug("Executing VMConnect command: {Command}", vmConnectCommand);
            ps.AddScript(vmConnectCommand);
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation($"Successfully launched VMConnect for VM: {vmSettings.VMName}");
        }

        public async Task EnableVirtualization(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Set-VMProcessor")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("ExposeVirtualizationExtensions", true);
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation($"Enabled virtualization extensions for VM: {vmSettings.VMName}");
        }

        public async Task EnableGuestServices(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Enable-VMIntegrationService")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Name", "Guest Service Interface");
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation($"Enabled Guest services for VM: {vmSettings.VMName}");
        }

        public async Task<string[]> FindExistingVmsByBaseNameAsync(string baseName, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Get-VM");
            var vms = await RunCommand(ps, cancellationToken);
            var matches = vms
                .Where(vm =>
                {
                    string name = vm.Properties["Name"]?.Value?.ToString() ?? "";
                    // Match exact base name or base name followed by _timestamp
                    return string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(baseName + "_", StringComparison.OrdinalIgnoreCase);
                })
                .Select(vm => vm.Properties["Name"]?.Value?.ToString())
                .Where(n => n != null)
                .ToArray();
            return matches;
        }

        public async Task<string[]> GetVmHardDiskPathsAsync(string vmName, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Get-VMHardDiskDrive")
                .AddParameter("VMName", vmName);
            var drives = await RunCommand(ps, cancellationToken);
            return drives
                .Select(d => d.Properties["Path"]?.Value?.ToString())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();
        }

        public async Task StopVMAsync(string vmName, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Stop-VM")
                .AddParameter("Name", vmName)
                .AddParameter("Force", true)
                .AddParameter("TurnOff", true);
            await Task.Run(ps.Invoke, cancellationToken);
            // Ignore errors (VM may already be off)
            if (ps.HadErrors)
            {
                _logger.LogWarning("Stop-VM had errors (VM may already be off): {Errors}",
                    string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
                ps.Streams.Error.Clear();
            }
        }

        public async Task RemoveVMAsync(string vmName, CancellationToken cancellationToken)
        {
            using var ps = CreatePowerShell();
            ps.AddCommand("Remove-VM")
                .AddParameter("Name", vmName)
                .AddParameter("Force", true);
            await RunCommand(ps, cancellationToken);
            _logger.LogInformation("Removed VM: {VMName}", vmName);
        }
    }
}
