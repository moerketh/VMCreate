using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    public interface IVmCreator
    {
        Task CreateVMAsync(VmSettings vmSettings, VmCustomizations vmCustomizations, string extractPath, GalleryItem galleryItem, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> downloadProgressInfo);
    }

    public class HyperVVmCreator : IVmCreator
    {
        private readonly string _defaultVhdxPath;
        private readonly MediaHandlerFactory _mediaHandlerFactory;
        private readonly IHyperVManager _hyperVManager;
        private readonly ILogger<HyperVVmCreator> _logger;
        private const int OriginalDiskScsiControllerLocation = 1;
        public HyperVVmCreator(ILogger<HyperVVmCreator> logger, MediaHandlerFactory mediaHandlerFactory, IHyperVManager hyperVManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mediaHandlerFactory = mediaHandlerFactory ?? throw new ArgumentNullException(nameof(mediaHandlerFactory));
            _hyperVManager = hyperVManager ?? throw new ArgumentNullException(nameof(hyperVManager));
            _defaultVhdxPath = GetDefaultVirtualHardDiskPath();
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

        public async Task CreateVMAsync(VmSettings vmSettings, VmCustomizations vmCustomizations, string sourceFile, GalleryItem item, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> createVMProgressInfo)
        {
            try
            {
                _logger.LogInformation("Starting VM creation for {VMName}", vmSettings.VMName);

                // Replace previous VM if requested
                if (vmSettings.ReplacePreviousVm)
                {
                    await ReplacePreviousVmAsync(vmSettings, cancellationToken);
                }
                    
                IMediaHandler mediaHandler = _mediaHandlerFactory.CreateHandler(item.FileType);
                string mediaPath = await mediaHandler.PrepareMediaAsync(sourceFile, _defaultVhdxPath, vmSettings, item, createVMProgressInfo, cancellationToken);
                
                string cloningIsoPath = vmSettings.CloningIsoPath;
                int detectedGeneration = mediaHandler.VmGeneration; // 1 for MBR, 2 for GPT
                const int targetGeneration = 2; // Always target Gen 2

                // Report detected generation so the UI can insert MBR-specific cards
                createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", DetectedGeneration = detectedGeneration.ToString() });

                await _hyperVManager.CreateVMAsync(vmSettings, _defaultVhdxPath, targetGeneration, cancellationToken);
                await _hyperVManager.SetVMLoginNotes(vmSettings, item.InitialUsername, item.InitialPassword, cancellationToken);
                //await _hyperVManager.AddNetworkAdapter(vmSettings, cancellationToken);
                await _hyperVManager.ConnectNetworkAdapter(vmSettings, cancellationToken);

                // Common settings: CPU, enhanced session, secure boot
                await _hyperVManager.SetCpuCount(vmSettings, cancellationToken);
                await _hyperVManager.DisableDynamicMemory(vmSettings, cancellationToken);
                await _hyperVManager.SetSecureBoot(vmSettings, cancellationToken);
                await _hyperVManager.EnableGuestServices(vmSettings, cancellationToken);

                if (detectedGeneration == 2)
                {
                    // Drive is GPT partitioned: Attach media directly as primary boot disk
                    await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);

                    if (vmCustomizations.ConfigureXrdp)
                    {
                        // GPT + xrdp: boot from customization ISO to chroot-install xrdp
                        await _hyperVManager.AddBootDvd(vmSettings, cloningIsoPath, cancellationToken);
                        await _hyperVManager.SetFirstBootToDvd(vmSettings, cancellationToken);
                    }
                    else
                    {
                        await _hyperVManager.SetFirstBootToHardDrive(vmSettings, cancellationToken);
                    }
                }
                else if (detectedGeneration == 1)
                {
                    // Drive is MBR partitioned: Add a new (larger) drive first so that we can copy data from old drive
                    await _hyperVManager.AddNewHardDrive(vmSettings, _defaultVhdxPath, cancellationToken);

                    // Attach old disk
                    await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);
                    _logger.LogInformation("Attached MBR disk as secondary for cloning: {MediaPath}", mediaPath);

                    // Attach cloning ISO and set as first boot device
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

                createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "StartVM" });
                await _hyperVManager.StartVM(vmSettings, cancellationToken);

                bool needsIsoBootCycle = detectedGeneration == 1 || (detectedGeneration == 2 && vmCustomizations.ConfigureXrdp);

                if (needsIsoBootCycle)
                {
                    // Report a Customize phase for Gen2 builds so the UI shows progress
                    if (detectedGeneration == 2)
                    {
                        createVMProgressInfo.Report(new CreateVMProgressInfo
                        {
                            Phase = "Customize",
                            DetectedGeneration = "2"
                        });
                    }

                    // ── KVP corruption mitigation ───────────────────────────────
                    // When a Gen2 VM boots, the Hyper-V host pushes network
                    // configuration (IP addresses, DNS, IPv6 multicast prefixes)
                    // through the same VMBus KVP channel that AddKvpItems uses.
                    // Both streams land in the guest's .kvp_pool_0 as fixed-size
                    // 2560-byte records (512 key + 2048 value).  If our WMI writes
                    // overlap with the network config burst the records get
                    // corrupted — e.g. key "DUMMY" becomes "DUMMYcastprefix" with
                    // ff02:: multicast data in the value field.
                    //
                    // This is NOT purely a timing issue — the first two records
                    // written via AddKvpItems are consistently corrupted even with
                    // a 10s delay, likely because hv_kvp_daemon is inactive and the
                    // kernel hv_utils module doesn't properly serialize VMBus writes
                    // across pools.
                    //
                    // Mitigation: wait 10s for boot to settle, then send two
                    // throwaway KVPs so real keys land in record slot 3+ where
                    // corruption doesn't reach.  The guest autorun.sh also retries
                    // reading VMCREATE_MODE for up to 30 seconds as belt-and-suspenders.
                    // ────────────────────────────────────────────────────────────
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

                    var kvp = new KvpHostToGuest();
                    await kvp.SendKVPToGuestAsync(vmSettings.VMName, "PADDING_1", "true", cancellationToken);
                    await kvp.SendKVPToGuestAsync(vmSettings.VMName, "PADDING_2", "true", cancellationToken);
#if DEBUG
                    //await kvp.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_DEBUG", "true", cancellationToken);
#endif
                    if (detectedGeneration == 2)
                    {
                        // Tell the ISO to run customize-only mode (skip disk cloning)
                        await kvp.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_MODE", "customize", cancellationToken);
                    }

                    if (vmCustomizations.ConfigureXrdp)
                        await kvp.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_XRDP", "true", cancellationToken);

                    // ── Monitor ISO progress and wait for shutdown ─────────────
                    const int shutdownTimeoutSeconds = 600;
                    bool shutDown;
                    var poller = new HyperVKVPPoller();

                    if (detectedGeneration == 1)
                    {
                        // Gen1: Monitor partclone disk clone progress, then wait for shutdown
                        await poller.PollKVPForProgressAsync(vmSettings.VMName, createVMProgressInfo, cancellationToken);

                        // Partclone done — transition from CloneDisk to Customize phase
                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "Customize" });

                        var kvpbase = new KvpBase();
                        shutDown = await kvpbase.WaitForVMShutdownAsync(vmSettings.VMName, cancellationToken, shutdownTimeoutSeconds);
                    }
                    else
                    {
                        // Gen2 customize-only: no partclone step.
                        // Poll WorkflowProgress KVP while waiting for the VM to shut
                        // itself down via OnSuccess=poweroff.target.
                        shutDown = await poller.WaitForShutdownWithProgressAsync(
                            vmSettings.VMName, createVMProgressInfo, cancellationToken, shutdownTimeoutSeconds);
                    }

                    if (!shutDown)
                    {
                        _logger.LogWarning("VM {VMName} did not shut down within {Timeout}s — collecting diagnostics.", vmSettings.VMName, shutdownTimeoutSeconds);

                        // Collect diagnostics from the ISO guest via PowerShell Direct
                        var diagnostics = await new GuestDiagnosticsCollector(_logger)
                            .CollectAsync(vmSettings.VMName, cancellationToken);

                        _logger.LogError("Guest diagnostics for {VMName}: {Summary}\n{RawOutput}",
                            vmSettings.VMName, diagnostics.Summary, diagnostics.RawOutput);

                        // Force-stop the stuck VM
                        await _hyperVManager.StopVMAsync(vmSettings.VMName, cancellationToken);
                        _logger.LogInformation("Force-stopped VM {VMName} after timeout.", vmSettings.VMName);

                        // Report the failure to the UI — this will set the phase card to Failed
                        createVMProgressInfo.Report(new CreateVMProgressInfo
                        {
                            Phase = "Customize",
                            ErrorMessage = diagnostics.Summary,
                            DiagnosticsLog = diagnostics.RawOutput
                        });

                        throw new Exception($"ISO customization timed out. {diagnostics.Summary}");
                    }

                    if (detectedGeneration == 1)
                    {
                        // Remove original disk
                        await _hyperVManager.RemoveHardDrive(vmSettings, OriginalDiskScsiControllerLocation, cancellationToken);
                    }

                    // Remove ISO
                    await _hyperVManager.RemoveBootDvd(vmSettings, cloningIsoPath, cancellationToken);

                    // Set hard drive as first boot device now that DVD and old disk are removed
                    await _hyperVManager.SetFirstBootToHardDrive(vmSettings, cancellationToken);
                }
                await _hyperVManager.SetEnhancedSession(vmSettings, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating VM: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Finds VMs whose name matches the base name (before the _timestamp suffix),
        /// stops them, collects their VHDX paths, removes the VMs, then deletes the VHDX files.
        /// </summary>
        private async Task ReplacePreviousVmAsync(VmSettings vmSettings, CancellationToken cancellationToken)
        {
            // The base name is the VMName before DeployPage appends _yyyyMMddHHmmss.
            // At this point VMName already has the timestamp, so strip it.
            string fullName = vmSettings.VMName;
            string baseName = fullName;
            int lastUnderscore = fullName.LastIndexOf('_');
            if (lastUnderscore > 0 && fullName.Length - lastUnderscore - 1 == 14)
            {
                // Looks like _yyyyMMddHHmmss
                baseName = fullName.Substring(0, lastUnderscore);
            }

            _logger.LogInformation("Looking for existing VMs matching base name: {BaseName}", baseName);
            string[] existingVms = await _hyperVManager.FindExistingVmsByBaseNameAsync(baseName, cancellationToken);

            foreach (var existingVmName in existingVms)
            {
                // Don't remove the VM we're about to create
                if (string.Equals(existingVmName, fullName, StringComparison.OrdinalIgnoreCase))
                    continue;

                _logger.LogInformation("Replacing existing VM: {ExistingVMName}", existingVmName);

                // Collect VHDX paths before removing the VM
                string[] vhdxPaths = await _hyperVManager.GetVmHardDiskPathsAsync(existingVmName, cancellationToken);

                // Stop and remove the VM
                await _hyperVManager.StopVMAsync(existingVmName, cancellationToken);
                await _hyperVManager.RemoveVMAsync(existingVmName, cancellationToken);

                // Delete VHDX files
                foreach (string vhdxPath in vhdxPaths)
                {
                    try
                    {
                        if (File.Exists(vhdxPath))
                        {
                            File.Delete(vhdxPath);
                            _logger.LogInformation("Deleted old VHDX: {VhdxPath}", vhdxPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete old VHDX {VhdxPath}: {Message}", vhdxPath, ex.Message);
                    }
                }

                // Also try to remove the VM's configuration folder (Hyper-V creates a folder under the VHD path)
                string vmConfigFolder = Path.Combine(_defaultVhdxPath, existingVmName);
                try
                {
                    if (Directory.Exists(vmConfigFolder))
                    {
                        Directory.Delete(vmConfigFolder, true);
                        _logger.LogInformation("Deleted old VM config folder: {Folder}", vmConfigFolder);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete VM config folder {Folder}: {Message}", vmConfigFolder, ex.Message);
                }
            }
        }
    }
}