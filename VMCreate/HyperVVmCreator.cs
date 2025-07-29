using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using System.Management.Automation;
using VMCreate;
using System.Threading;
using Microsoft.Extensions.Logging;
using VMCreateVM.MediaHandlers;
using System.Linq;

namespace VMCreateVM
{
    public interface IVmCreator
    {
        Task CreateVMAsync(VmSettings vmSettings, string extractPath, GalleryItem galleryItem, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> downloadProgressInfo);
    }

    public class HyperVVmCreator : IVmCreator
    {
        private readonly string vmPath;
        private readonly MediaHandlerFactory _mediaHandlerFactory;
        private readonly ILogger<HyperVVmCreator> _logger;

        public HyperVVmCreator(ILogger<HyperVVmCreator> logger, MediaHandlerFactory mediaHandlerFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mediaHandlerFactory = mediaHandlerFactory ?? throw new ArgumentNullException(nameof(mediaHandlerFactory));
            vmPath = GetDefaultVirtualHardDiskPath();
        }

        private string GetDefaultVirtualHardDiskPath()
        {
            string defaultPath = @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks";
            try
            {
                string registryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(registryPath))
                {
                    if (key != null)
                    {
                        string path = key.GetValue("DefaultVirtualHardDiskPath") as string;
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                        {
                            _logger.LogInformation("Using DefaultVirtualHardDiskPath from registry: {Path}", path);
                            return path;
                        }
                    }
                }
                _logger.LogInformation("DefaultVirtualHardDiskPath not found or invalid. Using default: {DefaultPath}", defaultPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading DefaultVirtualHardDiskPath: {Message}", ex.Message);
            }
            return defaultPath;
        }

        public async Task CreateVMAsync(VmSettings vmSettings, string sourceFile, GalleryItem item, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> createVMProgressInfo)
        {
            try
            {
                _logger.LogInformation("Starting VM creation for {VMName}", vmSettings.VMName);
                using (PowerShell ps = PowerShell.Create())
                {
                    IMediaHandler mediaHandler = _mediaHandlerFactory.CreateHandler(item.FileType);
                    await mediaHandler.PrepareMediaAsync(sourceFile, vmPath, item, createVMProgressInfo, cancellationToken);

                    int detectedGeneration = mediaHandler.VmGeneration;  // 1 for MBR, 2 for GPT
                    int targetGeneration = 2;  // Always target Gen 2

                    string mediaPath = Path.Combine(vmPath, Path.GetFileName(sourceFile));

                    // Create VM (always Gen 2)
                    ps.AddCommand("New-VM")
                        .AddParameter("Name", vmSettings.VMName)
                        .AddParameter("MemoryStartupBytes", vmSettings.MemoryMB * 1024L * 1024L)
                        .AddParameter("Path", vmPath)
                        .AddParameter("Generation", targetGeneration)
                        .AddParameter("NoVHD", true);  // No default VHD, we'll attach manually
                    await Task.Run(() => ps.Invoke());
                    _logger.LogInformation("Created Gen 2 VM: {VMName}", vmSettings.VMName);
                    if (ps.HadErrors) throw new Exception(ps.Streams.Error[0].ToString());

                    //Notes cannot be set on New-VM
                    ps.Commands.Clear();
                    ps.AddCommand("Set-VM")
                        .AddParameter("Name", vmSettings.VMName)
                        .AddParameter("Notes", $"Initial Username: {item.InitialUsername}\r\nInitial Password: {item.InitialPassword}");
                    await Task.Run(() => ps.Invoke());
                    if (ps.HadErrors) throw new Exception(ps.Streams.Error[0].ToString());
                    
                    //Disable dynamic memory on creation
                    ps.Commands.Clear();
                    ps.AddCommand("Set-VMMemory")
                        .AddParameter("VMName",  vmSettings.VMName)
                        .AddParameter("DynamicMemoryEnabled", false);
                    await Task.Run(() => ps.Invoke());
                    if (ps.HadErrors) throw new Exception(ps.Streams.Error[0].ToString());

                    ps.Commands.Clear();
                    ps.AddCommand("Add-VMNetworkAdapter")
                        .AddParameter("VMName", vmSettings.VMName)
                        .AddParameter("Name", "Network Adapter");
                    await Task.Run(() => ps.Invoke());
                    if (ps.HadErrors) throw new Exception(ps.Streams.Error[0].ToString());

                    ps.Commands.Clear();
                    ps.AddCommand("Connect-VMNetworkAdapter")
                        .AddParameter("VMName", vmSettings.VMName)
                        .AddParameter("SwitchName", "Default Switch");
                    await Task.Run(() => ps.Invoke());
                    _logger.LogInformation("Connected VM to Default Switch for internet access.");
                    if (ps.HadErrors) throw new Exception(ps.Streams.Error[0].ToString());

                    if (detectedGeneration == 2)
                    {
                        // Already GPT: Attach media directly as primary boot disk
                        ps.Commands.Clear();
                        ps.AddCommand("Add-VMHardDiskDrive")
                            .AddParameter("VMName", vmSettings.VMName)
                            .AddParameter("Path", mediaPath)
                            .AddParameter("ControllerType", "SCSI")
                            .AddParameter("ControllerNumber", 0)
                            .AddParameter("ControllerLocation", 0);
                        await Task.Run(() => ps.Invoke());
                        _logger.LogInformation("Attached GPT-compatible disk directly: {MediaPath}", mediaPath);

                        if (ps.HadErrors)
                        {
                            throw new Exception($"Failed to check DVD drive: {ps.Streams.Error[0]}");
                        }
                    }
                    else if (detectedGeneration == 1)
                    {
                        // MBR: Create new dynamic VHDX, attach as primary, old as secondary, attach cloning ISO
                        string newVhdPath = Path.Combine(vmPath, $"{vmSettings.VMName}.vhdx");
                        ps.Commands.Clear();
                        ps.AddCommand("New-VHD")
                            .AddParameter("Path", newVhdPath)
                            .AddParameter("Dynamic", true)
                            //.AddParameter("SizeBytes", vmSettings.VmDiskSizeGB * 1024L * 1024L * 1024L);  // User-specified max size
                            .AddParameter("SizeBytes", 150 * 1024L * 1024L * 1024L);
                        await Task.Run(() => ps.Invoke());
                        if (ps.HadErrors) throw new Exception(ps.Streams.Error[0].ToString());
                        _logger.LogInformation("Created new dynamic VHDX for cloning: {NewVhdPath}", newVhdPath);

                        if (ps.HadErrors)
                        {
                            throw new Exception($"Failed to check DVD drive: {ps.Streams.Error[0]}");
                        }

                        // Attach new VHDX as primary
                        ps.Commands.Clear();
                        ps.AddCommand("Add-VMHardDiskDrive")
                            .AddParameter("VMName", vmSettings.VMName)
                            .AddParameter("Path", newVhdPath)
                            .AddParameter("ControllerType", "SCSI")
                            .AddParameter("ControllerNumber", 0)
                            .AddParameter("ControllerLocation", 0);
                        await Task.Run(() => ps.Invoke());
                        _logger.LogInformation("Attached new dynamic VHDX for cloning: {NewVhdPath}", newVhdPath);

                        if (ps.HadErrors)
                        {
                            throw new Exception($"Failed to check DVD drive: {ps.Streams.Error[0]}");
                        }

                        // Attach old disk as secondary
                        await mediaHandler.AttachMediaAsync(ps, vmSettings.VMName, mediaPath, item, _logger);
                        _logger.LogInformation("Attached MBR disk as secondary for cloning: {MediaPath}", mediaPath);

                        if (ps.HadErrors)
                        {
                            throw new Exception($"Failed to check DVD drive: {ps.Streams.Error[0]}");
                        }

                        // Attach cloning ISO (set as first boot device)
                        string cloningIsoPath = "C:\\Users\\Thomas\\Desktop\\custom-autorun.iso";  // Pre-built path
                        var isoHandler = _mediaHandlerFactory.CreateHandler("ISO");
                        await isoHandler.AttachMediaAsync(ps, vmSettings.VMName, cloningIsoPath, item, _logger);

                        // Set ISO as first boot (for one-time clone)
                        ps.Commands.Clear();
                        ps.AddCommand("Get-VMDvdDrive")
                            .AddParameter("VMName", vmSettings.VMName);
                        var dvdDrive = (await Task.Run(() => ps.Invoke())).FirstOrDefault();
                        if (dvdDrive == null)
                        {
                            throw new Exception("No DVD drive found for VM. Ensure the cloning ISO is attached.");
                        }
                        ps.Commands.Clear();
                        ps.AddCommand("Set-VMFirmware")
                            .AddParameter("VMName", vmSettings.VMName)
                            .AddParameter("FirstBootDevice", dvdDrive);
                        await Task.Run(() => ps.Invoke());

                    }
                    else
                    {
                        throw new Exception($"Unsupported generation detected: {detectedGeneration}");
                    }

                    // Common settings: CPU, enhanced session, secure boot
                    ps.Commands.Clear();
                    ps.AddCommand("Set-VMProcessor")
                        .AddParameter("VMName", vmSettings.VMName)
                        .AddParameter("Count", vmSettings.CPUCount);
                    await Task.Run(() => ps.Invoke());

                    ps.Commands.Clear();
                    ps.AddCommand("Set-VM")
                        .AddParameter("VMName", vmSettings.VMName)
                        .AddParameter("EnhancedSessionTransportType", item.EnhancedSessionTransportType);
                    await Task.Run(() => ps.Invoke());

                    ps.Commands.Clear();
                    ps.AddCommand("Set-VMFirmware")
                        .AddParameter("VMName", vmSettings.VMName)
                        .AddParameter("EnableSecureBoot", item.SecureBoot == "true" ? "True" : "False")
                        .AddParameter("SecureBootTemplate", "Microsoft UEFI Certificate Authority");
                    await Task.Run(() => ps.Invoke());

                    // Enable nested virt if checked
                    if (Application.Current.MainWindow is MainWindow mainWindow && mainWindow.VirtualizationEnabledCheckBox.IsChecked == true)
                    {
                        ps.Commands.Clear();
                        ps.AddCommand("Set-VMProcessor")
                            .AddParameter("VMName", vmSettings.VMName)
                            .AddParameter("ExposeVirtualizationExtensions", true);
                        await Task.Run(() => ps.Invoke());
                        _logger.LogInformation("Enabled virtualization extensions for VM: {VMName}", vmSettings.VMName);
                    }
                    else
                    {
                        _logger.LogInformation("Virtualization extensions not enabled for VM: {VMName}", vmSettings.VMName);
                    }

                    //Boot VM
                    ps.Commands.Clear();
                    ps.AddCommand("Start-VM")
                        .AddParameter("VMName", vmSettings.VMName);
                    await Task.Run(() => ps.Invoke());

                    _logger.LogInformation("Launching VMConnect for VM: {VMName}", vmSettings.VMName);
                    ps.Commands.Clear();
                    string escapedVmName = vmSettings.VMName.Replace("'", "''");
                    string vmConnectCommand = $"& \"c:\\Windows\\SysNative\\vmconnect.exe\" localhost \"{escapedVmName}\"";
                    _logger.LogDebug("Executing VMConnect command: {Command}", vmConnectCommand);
                    ps.AddScript(vmConnectCommand);
                    await Task.Run(() => ps.Invoke());

                    if (ps.HadErrors)
                    {
                        string error = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                        _logger.LogWarning("Failed to launch VMConnect for VM {VMName}: {Error}", vmSettings.VMName, error);
                    }
                    else
                    {
                        _logger.LogInformation("Successfully launched VMConnect for VM: {VMName}", vmSettings.VMName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating VM: {Message}", ex.Message);
                throw;
            }
        }
    }
}