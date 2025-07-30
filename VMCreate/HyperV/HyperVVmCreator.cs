using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    public interface IVmCreator
    {
        Task CreateVMAsync(VmSettings vmSettings, string extractPath, GalleryItem galleryItem, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> downloadProgressInfo);
    }

    public class HyperVVmCreator : IVmCreator
    {
        private readonly string vmPath;
        private readonly MediaHandlerFactory _mediaHandlerFactory;
        private readonly IHyperVManager _hyperVManager;
        private readonly ILogger<HyperVVmCreator> _logger;

        public HyperVVmCreator(ILogger<HyperVVmCreator> logger, MediaHandlerFactory mediaHandlerFactory, IHyperVManager hyperVManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mediaHandlerFactory = mediaHandlerFactory ?? throw new ArgumentNullException(nameof(mediaHandlerFactory));
            _hyperVManager = hyperVManager ?? throw new ArgumentNullException(nameof(hyperVManager));
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
                    
                IMediaHandler mediaHandler = _mediaHandlerFactory.CreateHandler(item.FileType);
                await mediaHandler.PrepareMediaAsync(sourceFile, vmPath, item, createVMProgressInfo, cancellationToken);

                int detectedGeneration = mediaHandler.VmGeneration; // 1 for MBR, 2 for GPT
                const int targetGeneration = 2; // Always target Gen 2

                string mediaPath = Path.Combine(vmPath, Path.GetFileName(sourceFile));
                await _hyperVManager.CreateVMAsync(vmSettings, vmPath, targetGeneration, cancellationToken);
                await _hyperVManager.SetVMLoginNotes(vmSettings, item.InitialUsername, item.InitialPassword, cancellationToken);
                await _hyperVManager.AddNetworkAdapter(vmSettings, cancellationToken);
                await _hyperVManager.ConnectNetworkAdapter(vmSettings, cancellationToken);

                // Common settings: CPU, enhanced session, secure boot
                await _hyperVManager.SetCpuCount(vmSettings, cancellationToken);
                await _hyperVManager.SetEnhancedSession(vmSettings, cancellationToken);
                await _hyperVManager.SetSecureBoot(vmSettings, cancellationToken);


                if (detectedGeneration == 2)
                {
                    // Drive is GPT partitioned: Attach media directly as primary boot disk
                    await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);
                }
                else if (detectedGeneration == 1)
                {
                    // Drive is MBR partitioned: Add a new (larger) drive first so that we can copy data from old drive
                    await _hyperVManager.AddNewHardDrive(vmSettings, vmPath, cancellationToken);

                    // Attach old disk
                    await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);
                    _logger.LogInformation("Attached MBR disk as secondary for cloning: {MediaPath}", mediaPath);

                    // Attach cloning ISO and set as first boot device
                    string cloningIsoPath = "C:\\Users\\Thomas\\Desktop\\custom-autorun.iso"; // Update path as needed
                    await _hyperVManager.AddBootDvd(vmSettings, cloningIsoPath, cancellationToken);

                    // Set ISO as first boot (for one-time clone)
                    await _hyperVManager.SetFirstBootToDvd(vmSettings, cancellationToken);
                }
                else
                {
                    throw new Exception($"Unsupported generation detected: {detectedGeneration}");
                }

                if (vmSettings.VirtualizationEnabled)
                {
                    await _hyperVManager.EnableVirtualization(vmSettings, cancellationToken);
                }
                else
                {
                    _logger.LogInformation("Virtualization extensions not enabled for VM: {VMName}", vmSettings.VMName);
                }
                await _hyperVManager.StartVM(vmSettings, cancellationToken);
                await _hyperVManager.StartVMConnect(vmSettings, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating VM: {Message}", ex.Message);
                throw;
            }
        }        
    }
}