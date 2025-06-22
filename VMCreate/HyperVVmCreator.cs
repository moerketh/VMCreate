using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using System.Management.Automation;
using VMCreate;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace VMCreateVM
{
    public interface IVmCreator
    {
        Task CreateVMAsync(VmSettings vmSettings, string extractPath, GalleryItem galleryItem, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> downloadProgressInfo);
    }

    public class HyperVVmCreator : IVmCreator
    {
        private readonly string vmPath;
        private readonly DiskConverter _diskConverter;
        private readonly ILogger<HyperVVmCreator> _logger;
        private const long ScanStartOffset = 0x10000; // 64KB, minimum for VHDX headers
        private const long ScanEndOffset = 0xA00000; // 10MB, reasonable limit for data region
        private const int SectorSize = 512; // Standard disk sector size

        public HyperVVmCreator(ILogger<HyperVVmCreator> logger, DiskConverter diskConverter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _diskConverter = diskConverter ?? throw new ArgumentNullException(nameof(diskConverter));
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

        private async Task<string> DetectPartitionSchemeAsync(string diskPath)
        {
            _logger.LogInformation("Detecting partition scheme for disk: {DiskPath}", diskPath);
            try
            {
                using (FileStream stream = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bool mbrFound = false;
                    long mbrOffsetFound = 0;
                    long offset = ScanStartOffset;
                    byte[] buffer = new byte[SectorSize * 2]; // 512 for MBR + 512 for GPT

                    while (offset <= ScanEndOffset)
                    {
                        stream.Seek(offset, SeekOrigin.Begin);
                        int bytesRead = await stream.ReadAsync(buffer, 0, SectorSize * 2);
                        if (bytesRead < SectorSize * 2)
                        {
                            _logger.LogWarning("Reached end of file or insufficient data at offset {Offset}: {DiskPath}", offset, diskPath);
                            break;
                        }

                        // Check if MBR or GPT buffer is all zeros before logging
                        bool isMbrBufferZero = buffer.Take(SectorSize).All(b => b == 0);
                        bool isGptBufferZero = buffer.Skip(SectorSize).Take(SectorSize).All(b => b == 0);

                        // Log MBR buffer only if not all zeros
                        if (!isMbrBufferZero)
                        {
                            string mbrBufferHex = BitConverter.ToString(buffer, 0, Math.Min(16, SectorSize)).Replace("-", " ");
                        }

                        // Log GPT buffer only if not all zeros
                        if (!isGptBufferZero)
                        {
                            string gptBufferHex = BitConverter.ToString(buffer, SectorSize, Math.Min(16, SectorSize)).Replace("-", " ");
                        }

                        // Check MBR boot signature (0x55AA at offset 0x1FE)
                        bool isMbr = buffer[0x1FE] == 0x55 && buffer[0x1FF] == 0xAA;
                        if (isMbr)
                        {
                            // Check partition table (0x1BE–0x1FD) for protective MBR (single partition, type 0xEE)
                            bool isProtectiveMbr = false;
                            int partitionCount = 0;
                            for (int i = 0x1BE; i < 0x1FE; i += 16) // 4 partitions, 16 bytes each
                            {
                                byte partitionType = buffer[i + 4]; // Partition type at offset 4
                                if (partitionType != 0) // Non-zero type indicates a partition
                                {
                                    partitionCount++;
                                    if (partitionType == 0xEE) // GPT protective partition
                                    {
                                        isProtectiveMbr = true;
                                    }
                                }
                            }

                            if (isProtectiveMbr && partitionCount == 1)
                            {
                                // Log protective MBR details
                                string partitionTableHex = BitConverter.ToString(buffer, 0x1BE, 64).Replace("-", " ");
                                string signatureHex = BitConverter.ToString(buffer, 0x1FE, 2).Replace("-", " ");
                                _logger.LogDebug("Protective MBR (GPT) found at offset {Offset}, partition table: {TableHex}, signature: {SignatureHex}",
                                    offset, partitionTableHex, signatureHex);
                            }
                            else
                            {
                                mbrFound = true;
                                mbrOffsetFound = offset;
                                string partitionTableHex = BitConverter.ToString(buffer, 0x1BE, 64).Replace("-", " ");
                                string signatureHex = BitConverter.ToString(buffer, 0x1FE, 2).Replace("-", " ");
                                _logger.LogDebug("True MBR signature found at offset {Offset}, partition count: {Count}, partition table: {TableHex}, signature: {SignatureHex}",
                                    offset, partitionCount, partitionTableHex, signatureHex);
                            }
                        }

                        // Check GPT header ("EFI PART" at start of second 512 bytes)
                        string gptSignature = System.Text.Encoding.ASCII.GetString(buffer, SectorSize, 8);
                        if (gptSignature == "EFI PART")
                        {
                            string gptSignatureHex = BitConverter.ToString(buffer, SectorSize, 8).Replace("-", " ");
                            _logger.LogInformation("Detected GPT partition scheme at offset {Offset}, GPT signature: {SignatureHex}",
                                offset + SectorSize, gptSignatureHex);
                            return "GPT";
                        }

                        offset += SectorSize; // Move to next sector
                    }

                    // If true MBR signature was found and no GPT, assume MBR
                    if (mbrFound)
                    {
                        _logger.LogInformation("Detected MBR partition scheme at offset {Offset}", mbrOffsetFound);
                        return "MBR";
                    }

                    // Default to GPT if no clear signature
                    _logger.LogWarning("Could not determine partition scheme, defaulting to GPT");
                    return "GPT";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting partition scheme: {Message}", ex.Message);
                throw;
            }
        }

        public async Task CreateVMAsync(VmSettings vmSettings, string extractPath, GalleryItem item, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> createVMProgressInfo)
        {
            try
            {
                _logger.LogInformation("Starting VM creation for {VMName}", vmSettings.VMName);
                using (PowerShell ps = PowerShell.Create())
                {
                    // Prepare VHD
                    string vhdSourceFile = Path.Combine(extractPath, item.ArchiveRelativePath ?? throw new Exception("ArchiveRelativePath is null"));
                    string vhdDestFile = Path.Combine(vmPath, Path.GetFileNameWithoutExtension(item.ArchiveRelativePath) + ".vhdx");

                    _logger.LogDebug("Checking VHD source: {VhdSourceFile}", vhdSourceFile);
                    if (!File.Exists(vhdSourceFile))
                    {
                        _logger.LogError("VHD not found at: {VhdSourceFile}", vhdSourceFile);
                        throw new Exception($"VHD not found at {vhdSourceFile}");
                    }

                    // Convert VMDK to VHDX if necessary
                    if (Path.GetExtension(vhdSourceFile).ToLower() == ".vmdk")
                    {
                        _logger.LogInformation("Source is VMDK, converting to VHDX: {VhdDestFile}", vhdDestFile);
                        vhdSourceFile = await _diskConverter.ConvertToVhdxAsync(vhdSourceFile, vhdDestFile, createVMProgressInfo);
                        _logger.LogInformation("Converted VMDK to VHDX: {VhdSourceFile}", vhdSourceFile);
                    }
                    else
                    {
                        // Move non-VMDK file to destination
                        _logger.LogInformation("Source is not VMDK, moving to: {VhdDestFile}", vhdDestFile);
                        if (File.Exists(vhdDestFile))
                        {
                            File.Delete(vhdDestFile);
                            _logger.LogInformation("Deleted existing VHD at: {VhdDestFile}", vhdDestFile);
                        }
                        File.Move(vhdSourceFile, vhdDestFile);
                        _logger.LogInformation("Moved VHD to: {VhdDestFile}", vhdSourceFile);
                        vhdSourceFile = vhdDestFile;
                    }

                    // Detect partition scheme
                    string partitionScheme = await DetectPartitionSchemeAsync(vhdSourceFile);
                    int generation = partitionScheme == "GPT" ? 2 : 1;
                    _logger.LogInformation("Setting VM generation to {Generation} based on {PartitionScheme}", generation, partitionScheme);

                    // Create VM
                    ps.AddCommand("New-VM")
                        .AddParameter("Name", vmSettings.VMName)
                        .AddParameter("MemoryStartupBytes", vmSettings.MemoryMB * 1024L * 1024L)
                        .AddParameter("Path", vmPath)
                        .AddParameter("Generation", generation);
                    await Task.Run(() => ps.Invoke());
                    _logger.LogInformation("Created VM: {VMName}", vmSettings.VMName);

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

                    // Set secure boot (only for Gen2)
                    if (generation == 2)
                    {
                        ps.Commands.Clear();
                        ps.AddCommand("Set-VMFirmware")
                            .AddParameter("VMName", vmSettings.VMName)
                            .AddParameter("EnableSecureBoot", item.SecureBoot == "true" ? "On" : "Off");
                        await Task.Run(() => ps.Invoke());
                    }

                    // Attach VHD
                    _logger.LogDebug("Checking VHD destination: {VhdSourceFile}", vhdSourceFile);
                    if (File.Exists(vhdSourceFile))
                    {
                        ps.Commands.Clear();
                        ps.AddCommand("Add-VMHardDiskDrive")
                            .AddParameter("VMName", vmSettings.VMName)
                            .AddParameter("Path", vhdSourceFile)
                            .AddParameter("ControllerType", "SCSI");
                        await Task.Run(() => ps.Invoke());
                        _logger.LogInformation("Attached VHD: {VhdSourceFile}", vhdSourceFile);
                    }
                    else
                    {
                        _logger.LogError("VHD not found at: {VhdSourceFile}", vhdSourceFile);
                        throw new Exception($"VHD not found at {vhdSourceFile}");
                    }

                    // Enable virtualization extensions if checkbox is checked
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

                    // Launch VMConnect window
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