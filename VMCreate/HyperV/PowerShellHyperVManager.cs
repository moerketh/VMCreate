using CreateVM;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    public interface IHyperVManager
    {
        Task AddBootDvd(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken);
        Task AddExistingHardDrive(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken);
        Task AddNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken);
        Task AddNewHardDrive(VmSettings vmSettings, string vmPath, CancellationToken cancellationToken);
        Task ConnectNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken);
        Task CreateVMAsync(VmSettings vmSettings, string vmPath, int targetGeneration, CancellationToken cancellationToken);
        Task DisableDynamicMemory(VmSettings vmSettings, CancellationToken cancellationToken);
        Task EnableGuestServices(VmSettings vmSettings, CancellationToken cancellationToken);
        Task EnableVirtualization(VmSettings vmSettings, CancellationToken cancellationToken);
        Task RemoveBootDvd(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken);
        Task RemoveHardDrive(VmSettings vmSettings, int location, CancellationToken cancellationToken);
        Task SetCpuCount(VmSettings vmSettings, CancellationToken cancellationToken);
        Task SetEnhancedSession(VmSettings vmSettings, CancellationToken cancellationToken);
        Task SetFirstBootToDvd(VmSettings vmSettings, CancellationToken cancellationToken);
        Task SetFirstBootToHardDrive(VmSettings vmSettings, CancellationToken cancellationToken);
        Task SetSecureBoot(VmSettings vmSettings, CancellationToken cancellationToken);
        Task SetVMLoginNotes(VmSettings vmSettings, string initialUsername, string initialPassword, CancellationToken cancellationToken);
        Task StartVM(VmSettings vmSettings, CancellationToken cancellationToken);
        Task StartVMConnect(VmSettings vmSettings, CancellationToken cancellationToken);
    }

    internal class PowerShellHyperVManager : IHyperVManager
    {
        private readonly PowerShell _ps;
        private readonly ILogger<PowerShellHyperVManager> _logger;

        public PowerShellHyperVManager(ILogger<PowerShellHyperVManager> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ps = PowerShell.Create();
            _ps.AddCommand("Import-Module").AddParameter("Name", "Hyper-V").Invoke();
            if (_ps.HadErrors)
            {
                string error = string.Join("; ", _ps.Streams.Error.Select(e => e.ToString()));
                throw new Exception($"Failed to import Hyper-V module. Ensure that your system supports Hyper-V. {error}");
            }
            _ps.Commands.Clear();
        }

        public async Task CreateVMAsync(VmSettings vmSettings, string vmPath, int targetGeneration, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("New-VM")
                .AddParameter("Name", vmSettings.VMName)
                .AddParameter("MemoryStartupBytes", vmSettings.MemoryInMB * 1024L * 1024L)
                .AddParameter("Path", vmPath)
                .AddParameter("Generation", targetGeneration)
                .AddParameter("NoVHD", true);
            await Task.Run(() => _ps.Invoke(), cancellationToken);
            _logger.LogInformation("Created Gen 2 VM: {VMName}", vmSettings.VMName);
            if (_ps.HadErrors) throw new Exception(string.Join("; ", _ps.Streams.Error.Select(e => e.ToString())));
        }

        public async Task SetVMLoginNotes(VmSettings vmSettings, string initialUsername, string initialPassword, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Set-VM")
                .AddParameter("Name", vmSettings.VMName)
                .AddParameter("Notes", $"Initial Username: {initialUsername}\r\nInitial Password: {initialPassword}");
            await RunCommand(cancellationToken);

        }

        public async Task DisableDynamicMemory(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Set-VMMemory")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("DynamicMemoryEnabled", false);
            await RunCommand(cancellationToken);
        }

        public async Task AddNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Add-VMNetworkAdapter")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Name", "Network Adapter");
            await RunCommand(cancellationToken);
        }

        public async Task ConnectNetworkAdapter(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Connect-VMNetworkAdapter")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("SwitchName", "Default Switch");
            await RunCommand(cancellationToken);
            _logger.LogInformation("Connected VM to Default Switch for internet access.");
        }

        public async Task AddNewHardDrive(VmSettings vmSettings, string vmPath, CancellationToken cancellationToken)
        {
            // Create new dynamic VHDX
            string newVhdPath = Path.Combine(vmPath, $"{vmSettings.VMName}.vhdx");
            _ps.Commands.Clear();
            _ps.AddCommand("New-VHD")
                .AddParameter("Path", newVhdPath)
                .AddParameter("Dynamic", true)
                .AddParameter("SizeBytes", vmSettings.NewDriveSizeInGB * 1024L * 1024L * 1024L);
            await RunCommand(cancellationToken);
            _logger.LogInformation($"Created new dynamic VHDX for cloning: {newVhdPath}");

            // Attach new VHDX
            _ps.Commands.Clear();
            _ps.AddCommand("Add-VMHardDiskDrive")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Path", newVhdPath)
                .AddParameter("ControllerType", "SCSI");
            await RunCommand(cancellationToken);
            _logger.LogInformation($"Attached new dynamic VHDX for cloning: {newVhdPath}");
        }

        public async Task AddExistingHardDrive(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Add-VMHardDiskDrive")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Path", mediaPath)
                .AddParameter("ControllerType", "SCSI");
            await RunCommand(cancellationToken);
            _logger.LogInformation($"Attached VHDX: {mediaPath}");
        }

        public async Task RemoveHardDrive(VmSettings vmSettings, int location, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Get-VMHardDiskDrive")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("ControllerType", "SCSI")
                .AddParameter("ControllerNumber", "0")
                .AddParameter("ControllerLocation", location);
            await RunCommand(cancellationToken);
            _logger.LogInformation($"Detached disk at location: {location}");
        }

        public async Task AddBootDvd(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Checking for DVD drive on VM: {vmSettings.VMName}");
            _ps.Commands.Clear();
            _ps.AddCommand("Get-VMDvdDrive")
                .AddParameter("VMName", vmSettings.VMName);
            var results = await RunCommand(cancellationToken);

            if (results.Count == 0)
            {
                _logger.LogInformation($"No DVD drive found, adding one to VM: {vmSettings.VMName}");
                _ps.Commands.Clear();
                _ps.AddCommand("Add-VMDvdDrive")
                    .AddParameter("VMName", vmSettings.VMName);
                await RunCommand(cancellationToken);
                _logger.LogInformation($"Added DVD drive to VM: {vmSettings.VMName}");
            }
            else
            {
                _logger.LogInformation($"DVD drive already exists on VM: {vmSettings.VMName}");
            }

            _logger.LogInformation("Attaching ISO as DVD drive: {MediaPath}", mediaPath);
            _ps.Commands.Clear();
            _ps.AddCommand("Set-VMDvdDrive")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Path", mediaPath);
            await RunCommand(cancellationToken);
            _logger.LogInformation("Attached ISO to DVD drive: {MediaPath}", mediaPath);
        }

        public async Task RemoveBootDvd(VmSettings vmSettings, string mediaPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Detaching ISO from DVD drive: {MediaPath}", mediaPath);
            _ps.Commands.Clear();
            _ps.AddCommand("Set-VMDvdDrive")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Path", null);
            await RunCommand(cancellationToken);
            _logger.LogInformation("Detached ISO from DVD drive: {MediaPath}", mediaPath);
        }

        public async Task SetFirstBootToDvd(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Get-VMDvdDrive")
                .AddParameter("VMName", vmSettings.VMName);
            var dvdDrive = (await RunCommand(cancellationToken)).FirstOrDefault();
            if (dvdDrive == null)
            {
                throw new Exception("No DVD drive found for VM. Ensure the cloning ISO is attached.");
            }
            _ps.Commands.Clear();
            _ps.AddCommand("Set-VMFirmware")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("FirstBootDevice", dvdDrive);
            await RunCommand(cancellationToken);
        }

        public async Task SetFirstBootToHardDrive(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Get-VMHardDiskDrive")
                .AddParameter("VMName", vmSettings.VMName);
            var hardDrives = await RunCommand(cancellationToken);
            var firstDrive = hardDrives.FirstOrDefault();
            if (firstDrive == null)
            {
                throw new Exception("No hard disk drive found for VM. Ensure a VHDX is attached.");
            }
            _ps.Commands.Clear();
            _ps.AddCommand("Set-VMFirmware")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("FirstBootDevice", firstDrive);
            await RunCommand(cancellationToken);
            _logger.LogInformation("Set first boot device to hard drive for VM: {VMName}", vmSettings.VMName);
        }

        public async Task SetCpuCount(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Set-VMProcessor")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Count", vmSettings.CPUCount);
            await RunCommand(cancellationToken);
        }

        public async Task SetEnhancedSession(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Set-VM")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("EnhancedSessionTransportType", vmSettings.EnhancedSessionTransportType ?? "HvSocket");
            await RunCommand(cancellationToken);
        }

        public async Task SetSecureBoot(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Set-VMFirmware")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("EnableSecureBoot", vmSettings.SecureBoot.ToOnOff())
                .AddParameter("SecureBootTemplate", "MicrosoftUEFICertificateAuthority");
            await RunCommand(cancellationToken);
        }

        public async Task StartVM(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Start-VM")
                .AddParameter("VMName", vmSettings.VMName);
            await RunCommand(cancellationToken);
        }

        public async Task StartVMConnect(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            string escapedVmName = vmSettings.VMName.Replace("'", "''");
            string vmConnectCommand = $"& \"C:\\Windows\\System32\\vmconnect.exe\" localhost \"{escapedVmName}\"";
            _logger.LogDebug("Executing VMConnect command: {Command}", vmConnectCommand);
            _ps.AddScript(vmConnectCommand);
            await RunCommand(cancellationToken);
            _logger.LogInformation($"Successfully launched VMConnect for VM: {vmSettings.VMName}");
        }

        public async Task EnableVirtualization(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Set-VMProcessor")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("ExposeVirtualizationExtensions", true);
            await RunCommand(cancellationToken);
            _logger.LogInformation($"Enabled virtualization extensions for VM: {vmSettings.VMName}");
        }

        public async Task EnableGuestServices(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            _ps.Commands.Clear();
            _ps.AddCommand("Enable-VMIntegrationService")
                .AddParameter("VMName", vmSettings.VMName)
                .AddParameter("Name", "Guest Service Interface");
            await RunCommand(cancellationToken);
            _logger.LogInformation($"Enabled Guest services for VM: {vmSettings.VMName}");
        }

        private async Task<System.Collections.ObjectModel.Collection<PSObject>> RunCommand(CancellationToken cancellationToken)
        {
            var result = await Task.Run(_ps.Invoke, cancellationToken);
            if (_ps.HadErrors) throw new Exception(string.Join("; ", _ps.Streams.Error.Select(e => e.ToString())));
            return result;
        }        
    }
}
