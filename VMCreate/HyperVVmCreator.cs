using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using System.Management.Automation;
using VMCreate;
using System.Threading;

namespace VMCreateVM
{
    public interface IVmCreator
    {
        Task CreateVMAsync(VmSettings vmSettings, string extractPath, GalleryItem galleryItem, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> downloadProgressInfo);
    }

    public class HyperVVmCreator : IVmCreator
    {
        private readonly string logPath = Path.Combine(Path.GetTempPath(), "VMCreate.log");
        private readonly string vmPath;

        public HyperVVmCreator()
        {
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
                            WriteLog($"Using DefaultVirtualHardDiskPath from registry: {path}");
                            return path;
                        }
                    }
                }
                WriteLog($"DefaultVirtualHardDiskPath not found or invalid. Using default: {defaultPath}");
            }
            catch (Exception ex)
            {
                WriteLog($"Error reading DefaultVirtualHardDiskPath: {ex.Message}");
            }
            return defaultPath;
        }

        private void WriteLog(string message)
        {
            try
            {
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch { }
        }

        public async Task CreateVMAsync(VmSettings vmSettings, string extractPath, GalleryItem item, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> downloadProgressInfo)
        {
            try
            {
                WriteLog("Starting VM creation.");
                using (PowerShell ps = PowerShell.Create())
                {
                    // Create VM
                    ps.AddCommand("New-VM")
                        .AddParameter("Name", vmSettings.VMName)
                        .AddParameter("MemoryStartupBytes", vmSettings.MemoryMB * 1024L * 1024L)
                        .AddParameter("Path", vmPath)
                        .AddParameter("Generation", 2);
                    await Task.Run(() => ps.Invoke());
                    WriteLog($"Created VM: {vmSettings.VMName}");

                    if (ps.HadErrors)
                    {
                        throw new Exception(ps.Streams.Error[0].ToString());
                    }

                    // Set CPU count
                    ps.Commands.Clear();
                    ps.AddCommand("Set-VMProcessor")
                        .AddParameter("VMName", vmSettings.VMName)
                        .AddParameter("Count", vmSettings.CPUCount);
                    await Task.Run(() => ps.Invoke());

                    // Set enhanced session
                    ps.Commands.Clear();
                    ps.AddCommand("Set-VM")
                        .AddParameter("VMName", vmSettings.VMName)
                        .AddParameter("EnhancedSessionTransportType", item.EnhancedSessionTransportType);
                    await Task.Run(() => ps.Invoke());

                    // Set secure boot
                    ps.Commands.Clear();
                    ps.AddCommand("Set-VMFirmware")
                        .AddParameter("VMName", vmSettings.VMName)
                        .AddParameter("EnableSecureBoot", item.SecureBoot == "true" ? "On" : "Off");
                    await Task.Run(() => ps.Invoke());

                    // Move VHD to vmPath
                    string vhdSourceFile = Path.Combine(extractPath, item.ArchiveRelativePath ?? throw new Exception("ArchiveRelativePath is null"));
                    string vhdDestFile = Path.Combine(vmPath, Path.GetFileName(item.ArchiveRelativePath));
                    WriteLog($"Checking VHD source: {vhdSourceFile}");
                    if (File.Exists(vhdSourceFile))
                    {
                        WriteLog($"Source VHD exists. Moving to: {vhdDestFile}");
                        if (File.Exists(vhdDestFile))
                        {
                            File.Delete(vhdDestFile);
                            WriteLog($"Deleted existing VHD at: {vhdDestFile}");
                        }
                        File.Move(vhdSourceFile, vhdDestFile);
                        WriteLog($"Moved VHD to: {vhdDestFile}");
                    }
                    else
                    {
                        WriteLog($"VHD not found at: {vhdSourceFile}");
                        throw new Exception($"VHD not found at {vhdSourceFile}");
                    }

                    // Attach VHD
                    WriteLog($"Checking VHD destination: {vhdDestFile}");
                    if (File.Exists(vhdDestFile))
                    {
                        ps.Commands.Clear();
                        ps.AddCommand("Add-VMHardDiskDrive")
                            .AddParameter("VMName", vmSettings.VMName)
                            .AddParameter("Path", vhdDestFile)
                            .AddParameter("ControllerType", "SCSI");
                        await Task.Run(() => ps.Invoke());
                        WriteLog($"Attached VHD: {vhdDestFile}");
                    }
                    else
                    {
                        WriteLog($"VHD not found at: {vhdDestFile}");
                        throw new Exception($"VHD not found at {vhdDestFile}");
                    }

                    // Enable virtualization extensions if checkbox is checked
                    if (Application.Current.MainWindow is MainWindow mainWindow && mainWindow.VirtualizationEnabledCheckBox.IsChecked == true)
                    {
                        ps.Commands.Clear();
                        ps.AddCommand("Set-VMProcessor")
                            .AddParameter("VMName", vmSettings.VMName)
                            .AddParameter("ExposeVirtualizationExtensions", true);
                        await Task.Run(() => ps.Invoke());
                        WriteLog($"Enabled virtualization extensions for VM: {vmSettings.VMName}");
                    }
                    else
                    {
                        WriteLog("Virtualization extensions not enabled for VM creation.");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error creating VM: {ex.Message}");
                throw;
            }
        }
    }
}