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

                    int generation = mediaHandler.VmGeneration;
                    ps.AddCommand("New-VM")
                        .AddParameter("Name", vmSettings.VMName)
                        .AddParameter("MemoryStartupBytes", vmSettings.MemoryMB * 1024L * 1024L)
                        .AddParameter("Path", vmPath)
                        .AddParameter("Generation", generation);
                    await Task.Run(() => ps.Invoke());
                    _logger.LogInformation("Created VM: {VMName} with generation {Generation}", vmSettings.VMName, generation);

                    if (ps.HadErrors)
                    {
                        throw new Exception(ps.Streams.Error[0].ToString());
                    }

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

                    if (generation == 2)
                    {
                        ps.Commands.Clear();
                        ps.AddCommand("Set-VMFirmware")
                            .AddParameter("VMName", vmSettings.VMName)
                            .AddParameter("EnableSecureBoot", item.SecureBoot == "true" ? "On" : "Off");
                        await Task.Run(() => ps.Invoke());
                    }

                    string mediaPath = Path.Combine(vmPath, Path.GetFileName(sourceFile));

                    await mediaHandler.AttachMediaAsync(ps, vmSettings.VMName, mediaPath, item, _logger);

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