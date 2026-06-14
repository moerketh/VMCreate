using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.MediaHandlers;

namespace VMCreate
{
    public interface IVmCreator
    {
        /// <summary>
        /// Returns the directory where VM disk files should be created.
        /// Used by the extraction flow to avoid a wasteful temp→destination copy.
        /// </summary>
        string GetVirtualHardDiskPath();

        /// <summary>
        /// Returns the per-VM subdirectory under GetVirtualHardDiskPath() where archives
        /// and intermediate disk files for this VM should be extracted.
        /// </summary>
        string GetVirtualHardDiskPath(string vmName);

        Task CreateVMAsync(VmSettings vmSettings, VmCustomizations vmCustomizations, string extractPath, GalleryItem galleryItem, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> downloadProgressInfo);
    }

    public class HyperVVmCreator : IVmCreator
    {
        private readonly string _defaultVmPath;
        private readonly string _defaultVhdxPath;
        private readonly IMediaHandlerFactory _mediaHandlerFactory;
        private readonly IHyperVManager _hyperVManager;
        private readonly ILogger<HyperVVmCreator> _logger;
        private readonly ISshKeyManager _sshKeyManager;
        private readonly IKvpSender _kvpSender;
        private readonly IKvpPoller _kvpPoller;
        private readonly IVmShutdownWatcher _shutdownWatcher;
        private readonly IGuestDiagnosticsCollector _diagnosticsCollector;
        private readonly IGuestShellFactory _guestShellFactory;
        private readonly ICloningIsoDownloader _cloningIsoDownloader;
        private readonly IUnattendInjector _unattendInjector;
        private readonly IEnumerable<ICustomizationStep> _customizationSteps;
        private const int OriginalDiskScsiControllerLocation = 1;
        public HyperVVmCreator(
            ILogger<HyperVVmCreator> logger,
            IMediaHandlerFactory mediaHandlerFactory,
            IHyperVManager hyperVManager,
            IEnumerable<ICustomizationStep> customizationSteps,
            ISshKeyManager sshKeyManager,
            IKvpSender kvpSender,
            IKvpPoller kvpPoller,
            IVmShutdownWatcher shutdownWatcher,
            IGuestDiagnosticsCollector diagnosticsCollector,
            IGuestShellFactory guestShellFactory,
            ICloningIsoDownloader cloningIsoDownloader,
            IUnattendInjector unattendInjector)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mediaHandlerFactory = mediaHandlerFactory ?? throw new ArgumentNullException(nameof(mediaHandlerFactory));
            _hyperVManager = hyperVManager ?? throw new ArgumentNullException(nameof(hyperVManager));
            _customizationSteps = customizationSteps ?? Array.Empty<ICustomizationStep>();
            _sshKeyManager = sshKeyManager ?? throw new ArgumentNullException(nameof(sshKeyManager));
            _kvpSender = kvpSender ?? throw new ArgumentNullException(nameof(kvpSender));
            _kvpPoller = kvpPoller ?? throw new ArgumentNullException(nameof(kvpPoller));
            _shutdownWatcher = shutdownWatcher ?? throw new ArgumentNullException(nameof(shutdownWatcher));
            _diagnosticsCollector = diagnosticsCollector ?? throw new ArgumentNullException(nameof(diagnosticsCollector));
            _guestShellFactory = guestShellFactory ?? throw new ArgumentNullException(nameof(guestShellFactory));
            _cloningIsoDownloader = cloningIsoDownloader ?? throw new ArgumentNullException(nameof(cloningIsoDownloader));
            _unattendInjector = unattendInjector ?? throw new ArgumentNullException(nameof(unattendInjector));
            _defaultVmPath = GetDefaultVirtualMachinePath();
            _defaultVhdxPath = GetDefaultVirtualHardDiskPath();
        }

        private string GetDefaultVirtualMachinePath()
        {
            string[] defaultPaths = new[]
            {
                @"C:\ProgramData\Microsoft\Windows\Hyper-V",
                @"C:\Users\Public\Documents\Hyper-V\Virtual Machines"
            };

            string[] registryPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization",
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtual Machine Manager"
            };

            string[] valueNames = new[]
            { "DefaultExternalDataRoot", "DefaultVirtualMachinePath", "VirtualMachinePath" };

            foreach (string regPath in registryPaths)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(regPath))
                    {
                        if (key == null)
                        {
                            _logger.LogDebug("Registry key not found: {Key}", regPath);
                            continue;
                        }

                        foreach (string valName in valueNames)
                        {
                            object rawValue = key.GetValue(valName);
                            if (rawValue == null)
                            {
                                _logger.LogDebug("Value {ValueName} not found under {Key}", valName, regPath);
                                continue;
                            }

                            string path = rawValue.ToString();
                            _logger.LogDebug("Read {ValueName} from {Key}: '{Path}'", valName, regPath, path);

                            if (string.IsNullOrWhiteSpace(path))
                            {
                                _logger.LogDebug("Value {ValueName} is empty under {Key}", valName, regPath);
                                continue;
                            }

                            char[] trimChars = { '\\', '/' };
                            path = path.TrimEnd(trimChars);

                            if (Directory.Exists(path))
                            {
                                _logger.LogInformation("Using VM path from registry [{Key} > {ValueName}]: {Path}", regPath, valName, path);
                                return path;
                            }
                            else
                            {
                                _logger.LogWarning("Registry path does not exist: {Path} (from {Key} > {ValueName})", path, regPath, valName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading registry key {Key}: {Message}", regPath, ex.Message);
                }
            }

            foreach (string fallback in defaultPaths)
            {
                if (Directory.Exists(fallback))
                {
                    _logger.LogInformation("Using default VM path: {Path}", fallback);
                    return fallback;
                }
            }

            _logger.LogError("No valid VM path found in registry or default locations. Using last fallback: {Path}", defaultPaths[0]);
            return defaultPaths[0];
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

        /// <summary>
        /// Resolves the DNS server addresses configured on the Windows host machine.
        /// Returns a comma-separated list of IP addresses from all active network
        /// interfaces that have an IPv4 default gateway, falling back to all
        /// operational interfaces if none have a gateway.
        /// </summary>
        private string ResolveHostDnsServers()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                              && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                // Prefer interfaces that have a default gateway (most likely the active connection)
                var gatewayInterfaces = interfaces
                    .Where(ni => ni.GetIPProperties().GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == AddressFamily.InterNetwork))
                    .ToList();

                var source = gatewayInterfaces.Count > 0 ? gatewayInterfaces : interfaces;

                var dnsAddresses = source
                    .SelectMany(ni => ni.GetIPProperties().DnsAddresses)
                    .Where(addr => addr.AddressFamily == AddressFamily.InterNetwork
                                || addr.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(addr => addr.ToString())
                    .Distinct()
                    .ToList();

                if (dnsAddresses.Count > 0)
                {
                    _logger.LogDebug("Resolved host DNS servers: {DnsServers}", string.Join(", ", dnsAddresses));
                    return string.Join(",", dnsAddresses);
                }

                _logger.LogWarning("No DNS servers found on any active network interface");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve host DNS servers");
            }
            return null;
        }

        public string GetVirtualHardDiskPath() => _defaultVhdxPath;

        /// <summary>
        /// Returns a per-VM subdirectory for extracting archives and intermediate disk files.
        /// This avoids collisions with disk files still in use by a previously created VM.
        /// </summary>
        public string GetVirtualHardDiskPath(string vmName)
        {
            if (string.IsNullOrWhiteSpace(vmName))
                throw new ArgumentException("VM name is required.", nameof(vmName));
            return Path.Combine(_defaultVhdxPath, vmName);
        }

        public async Task CreateVMAsync(VmSettings vmSettings, VmCustomizations vmCustomizations, string sourceFile, GalleryItem item, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> createVMProgressInfo)
        {
            bool vmCreated = false;
            try
            {
                _logger.LogInformation("Starting VM creation for {VMName}", vmSettings.VMName);

                // Replace previous VM if requested
                if (vmSettings.ReplacePreviousVm)
                {
                    await ReplacePreviousVmAsync(vmSettings, cancellationToken);
                }
                    
                // Determine the media type from the actual file on disk rather than
                // the gallery item's DiskUri.  After extraction the sourceFile points at
                // the real disk (e.g. a .vmdk extracted from an OVA).
                string actualFileType = DiskFileDetector.DetectFileType(sourceFile);
                if (actualFileType is "Unknown" or "Other")
                    actualFileType = item.FileType;  // fallback to gallery metadata

                IMediaHandler mediaHandler = _mediaHandlerFactory.CreateHandler(actualFileType);
                bool isIsoMedia = mediaHandler is IsoMediaHandler;

                // Start the cloning ISO download in parallel with media preparation.
                // Only disk-image flows (not ISO installer, not native Hyper-V) need it.
                Task ensureIsoTask = null;
                if (!isIsoMedia && !item.IsNativeHyperV)
                {
                    ensureIsoTask = _cloningIsoDownloader.EnsureIsoAsync(vmSettings, cancellationToken, createVMProgressInfo);
                }

                string mediaPath = await mediaHandler.PrepareMediaAsync(sourceFile, _defaultVhdxPath, vmSettings, item, createVMProgressInfo, cancellationToken);

                if (isIsoMedia)
                {
                    // ── ISO installer flow ─────────────────────────────────────
                    // The downloaded file is an ISO image. We create an empty VHDX
                    // for the OS to install onto, attach the ISO as a DVD, and boot
                    // from the DVD so the user can run the installer interactively.
                    const int targetGeneration = 2;

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM" });

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_CreateVMSkeleton" });
                    await _hyperVManager.CreateVMAsync(vmSettings, _defaultVmPath, targetGeneration, cancellationToken);
                    vmCreated = true;
                    await _hyperVManager.SetVMLoginNotes(vmSettings, item.InitialUsername, item.InitialPassword, cancellationToken);

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConnectNic" });
                    await _hyperVManager.ConnectNetworkAdapter(vmSettings, cancellationToken);

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConfigureHardware" });
                    await _hyperVManager.SetCpuCount(vmSettings, cancellationToken);
                    await _hyperVManager.DisableDynamicMemory(vmSettings, cancellationToken);

                    // Use the SecureBootTemplate from the GalleryItem if specified
                    // (e.g. "MicrosoftWindows" for Windows VMs), otherwise use the default
                    if (!string.IsNullOrEmpty(item.SecureBootTemplate))
                        vmSettings.SecureBootTemplate = item.SecureBootTemplate;
                    await _hyperVManager.SetSecureBoot(vmSettings, cancellationToken);

                    if (vmCustomizations.EnableIntegrationServices)
                        await _hyperVManager.EnableGuestServices(vmSettings, cancellationToken);

                    // Create an empty boot disk for the installer to target
                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachDisk" });
                    await _hyperVManager.AddNewHardDrive(vmSettings, _defaultVhdxPath, cancellationToken);

                    // Attach the ISO as a DVD drive and boot from it
                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachBootDvd" });
                    await _hyperVManager.AddBootDvd(vmSettings, mediaPath, cancellationToken);

                    // For Windows ISOs, create and attach an unattend.xml ISO for
                    // automated installation (no interactive setup required).
                    string unattendIsoPath = null;
                    if (item.IsWindows)
                    {
                        _logger.LogInformation("Creating unattend.xml ISO for automated Windows installation on VM {VMName}", vmSettings.VMName);
                        unattendIsoPath = Path.Combine(_defaultVhdxPath, $"{vmSettings.VMName}_unattend.iso");
                        try
                        {
                            UnattendFloppyBuilder.BuildUnattendIsoFromContent(
                                unattendIsoPath,
                                File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Unattend", "autounattend.xml")),
                                _logger);
                            await _hyperVManager.AddBootDvd(vmSettings, unattendIsoPath, cancellationToken);
                            _logger.LogInformation("Attached unattend.xml ISO to VM {VMName}", vmSettings.VMName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to create unattend.xml ISO for VM {VMName} — Windows installation will require manual input", vmSettings.VMName);
                            unattendIsoPath = null;
                        }
                    }

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_SetBootOrder" });
                    await _hyperVManager.SetFirstBootToDvd(vmSettings, cancellationToken);

                    if (vmSettings.VirtualizationEnabled)
                    {
                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_EnableNestedVirt" });
                        await _hyperVManager.EnableVirtualization(vmSettings, cancellationToken);
                    }

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "StartVM" });
                    await _hyperVManager.StartVM(vmSettings, cancellationToken);

                    if (vmCustomizations.EnableIntegrationServices)
                        await _hyperVManager.SetEnhancedSession(vmSettings, cancellationToken);

                    // ── Windows ISO: post-boot customization via PowerShell Direct ──
                    // For Windows VMs, wait for the unattended installation to complete,
                    // then run post-boot customization steps using PowerShell Direct.
                    if (item.IsWindows)
                        await RunWindowsPostBootCustomizationAsync(vmSettings, item, vmCustomizations, createVMProgressInfo, cancellationToken);

                    // Clean up unattend ISO if we created one
                    if (unattendIsoPath != null && File.Exists(unattendIsoPath))
                    {
                        try
                        {
                            // Detach the unattend DVD first
                            await _hyperVManager.RemoveBootDvd(vmSettings, unattendIsoPath, cancellationToken);
                            File.Delete(unattendIsoPath);
                            _logger.LogInformation("Cleaned up unattend ISO for VM {VMName}", vmSettings.VMName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to clean up unattend ISO for VM {VMName}", vmSettings.VMName);
                        }
                    }

                    // ISO flow is done — the user completes the installation interactively (Linux)
                    // or the unattended installation + post-boot steps are done (Windows).
                    return;
                }

                // ── Disk-image flow (VMDK / QCOW2 / VHDX) ────────────────────

                // ── Native Hyper-V image (e.g. Windows 11 dev environment) ────
                // These images are pre-built for Hyper-V and need no conversion.
                if (item.IsNativeHyperV)
                {
                    int detectedGen = mediaHandler.VmGeneration;
                    const int targetGenNative = 2;

                    // Windows native images need the Windows template; Linux images
                    // (SecureBoot = "false") keep the default UEFI CA template.
                    // GalleryItem.SecureBootTemplate takes precedence if set.
                    if (!string.IsNullOrEmpty(item.SecureBootTemplate))
                        vmSettings.SecureBootTemplate = item.SecureBootTemplate;
                    else if (!string.Equals(item.SecureBoot, "false", StringComparison.OrdinalIgnoreCase))
                        vmSettings.SecureBootTemplate = "MicrosoftWindows";

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", DetectedGeneration = detectedGen.ToString() });

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_CreateVMSkeleton" });
                    await _hyperVManager.CreateVMAsync(vmSettings, _defaultVmPath, targetGenNative, cancellationToken);
                    vmCreated = true;
                    await _hyperVManager.SetVMLoginNotes(vmSettings, item.InitialUsername, item.InitialPassword, cancellationToken);

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConnectNic" });
                    await _hyperVManager.ConnectNetworkAdapter(vmSettings, cancellationToken);

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConfigureHardware" });
                    await _hyperVManager.SetCpuCount(vmSettings, cancellationToken);
                    await _hyperVManager.DisableDynamicMemory(vmSettings, cancellationToken);
                    await _hyperVManager.SetSecureBoot(vmSettings, cancellationToken);
                    if (vmCustomizations.EnableIntegrationServices)
                        await _hyperVManager.EnableGuestServices(vmSettings, cancellationToken);

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachDisk" });

                    // ── Windows native Hyper-V: the "flare" account is provisioned during OOBE by
                    // the unattend.xml injected into the VHDX below. Mount-VHD requires a full
                    // Administrator token, so the injection runs in a one-shot elevated child
                    // process (one UAC prompt). Microsoft dev VHDX files are often read-only —
                    // clear the attribute (a plain, non-admin file operation) so the guest can
                    // boot and write to its own disk, and so Mount-VHD can open the VHDX read-write.
                    if (item.IsWindows)
                    {
                        var vhdxInfo = new FileInfo(mediaPath);
                        if (vhdxInfo.IsReadOnly)
                        {
                            _logger.LogInformation("Clearing read-only attribute on VHDX for VM {VMName}: {VhdxPath}", vmSettings.VMName, mediaPath);
                            vhdxInfo.IsReadOnly = false;
                        }

                        // Inject unattend.xml into the VHDX (elevated child process).
                        // This provisions the flare account, auto-logon, and RDP so that
                        // OOBE runs unattended and PowerShell Direct can connect afterwards.
                        bool injected = await _unattendInjector.InjectAsync(mediaPath, cancellationToken);
                        if (!injected)
                            throw new InvalidOperationException(
                                "Administrator approval is required to prepare the Windows image (unattend injection). Deployment cancelled.");
                    }

                    await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_SetBootOrder" });
                    await _hyperVManager.SetFirstBootToHardDrive(vmSettings, cancellationToken);

                    if (vmSettings.VirtualizationEnabled)
                    {
                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_EnableNestedVirt" });
                        await _hyperVManager.EnableVirtualization(vmSettings, cancellationToken);
                    }

                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "StartVM" });
                    await _hyperVManager.StartVM(vmSettings, cancellationToken);

                    if (vmCustomizations.EnableIntegrationServices)
                        await _hyperVManager.SetEnhancedSession(vmSettings, cancellationToken);

                    // ── Windows native Hyper-V: post-boot customization via PowerShell Direct ──
                    // For Windows VMs (e.g. FLARE VM), run post-boot customization steps
                    // using PowerShell Direct after the VM boots.
                    if (item.IsWindows)
                        await RunWindowsPostBootCustomizationAsync(vmSettings, item, vmCustomizations, createVMProgressInfo, cancellationToken);

                    // Native Hyper-V flow is done.
                    return;
                }


                // Await the parallel cloning ISO download (started before PrepareMediaAsync)
                await ensureIsoTask;
                string cloningIsoPath = vmSettings.CloningIsoPath;
                int detectedGeneration = mediaHandler.VmGeneration; // 1 for MBR, 2 for GPT
                const int targetGen = 2; // Always target Gen 2

                // Report detected generation so the UI can insert MBR-specific cards
                createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", DetectedGeneration = detectedGeneration.ToString() });

                createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_CreateVMSkeleton" });
                await _hyperVManager.CreateVMAsync(vmSettings, _defaultVmPath, targetGen, cancellationToken);
                vmCreated = true;
                await _hyperVManager.SetVMLoginNotes(vmSettings, item.InitialUsername, item.InitialPassword, cancellationToken);

                createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConnectNic" });
                //await _hyperVManager.AddNetworkAdapter(vmSettings, cancellationToken);
                await _hyperVManager.ConnectNetworkAdapter(vmSettings, cancellationToken);

                // Common settings: CPU, enhanced session, secure boot
                createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_ConfigureHardware" });
                await _hyperVManager.SetCpuCount(vmSettings, cancellationToken);
                await _hyperVManager.DisableDynamicMemory(vmSettings, cancellationToken);
                await _hyperVManager.SetSecureBoot(vmSettings, cancellationToken);
                if (vmCustomizations.EnableIntegrationServices)
                    await _hyperVManager.EnableGuestServices(vmSettings, cancellationToken);

                if (detectedGeneration == 2)
                {
                    // Drive is GPT partitioned: Attach media directly as primary boot disk
                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachDisk" });
                    await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);

                    bool needsIsoBoot = vmCustomizations.ConfigureXrdp
                        || vmCustomizations.ConfigureHtbVpn
                        || vmCustomizations.SyncTimezone;

                    if (needsIsoBoot)
                    {
                        // GPT + customization: boot from customization ISO to chroot-install packages
                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachBootDvd" });
                        await _hyperVManager.AddBootDvd(vmSettings, cloningIsoPath, cancellationToken);
                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_SetBootOrder" });
                        await _hyperVManager.SetFirstBootToDvd(vmSettings, cancellationToken);
                    }
                    else
                    {
                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_SetBootOrder" });
                        await _hyperVManager.SetFirstBootToHardDrive(vmSettings, cancellationToken);
                    }
                }
                else if (detectedGeneration == 1)
                {
                    // Drive is MBR partitioned: Add a new (larger) drive first so that we can copy data from old drive
                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachDisk" });
                    await _hyperVManager.AddNewHardDrive(vmSettings, _defaultVhdxPath, cancellationToken);

                    // Attach old disk
                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachCloneDisk" });
                    await _hyperVManager.AddExistingHardDrive(vmSettings, mediaPath, cancellationToken);
                    _logger.LogInformation("Attached MBR disk as secondary for cloning: {MediaPath}", mediaPath);

                    // Attach cloning ISO and set as first boot device
                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_AttachBootDvd" });
                    await _hyperVManager.AddBootDvd(vmSettings, cloningIsoPath, cancellationToken);

                    // Set ISO as first boot (for one-time clone)
                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_SetBootOrder" });
                    await _hyperVManager.SetFirstBootToDvd(vmSettings, cancellationToken);
                }
                else
                {
                    throw new Exception($"Unsupported generation detected: {detectedGeneration}");
                }

                if (vmSettings.VirtualizationEnabled)
                {
                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "CreateVM", SubStep = "Sub_EnableNestedVirt" });
                    await _hyperVManager.EnableVirtualization(vmSettings, cancellationToken);
                }
                else
                {
                    _logger.LogInformation("Virtualization extensions not enabled for VM: {VMName}", vmSettings.VMName);
                }

                createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "StartVM" });
                await _hyperVManager.StartVM(vmSettings, cancellationToken);

                bool needsPostBoot = _customizationSteps
                    .Any(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == StepPlatform.Linux && s.IsApplicable(item, vmCustomizations));
                bool needsIsoBootCycle = detectedGeneration == 1
                    || (detectedGeneration == 2 && (vmCustomizations.ConfigureXrdp || needsPostBoot));

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

                    await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "PADDING_1", "true", cancellationToken);
                    await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "PADDING_2", "true", cancellationToken);

                    // ── SSH key first ────────────────────────────────────────
                    // Send the SSH public key as early as possible so the ISO's
                    // inject-ssh-key.service can install it before autorun starts.
                    // This lets us SSH in to debug even when the main workflow hangs.
                    await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "PADDING_3", "true", cancellationToken);

                    string sshPublicKey;
                    if (!string.IsNullOrEmpty(vmCustomizations.CustomSshPublicKeyPath))
                        sshPublicKey = await _sshKeyManager.ReadPublicKeyAsync(vmCustomizations.CustomSshPublicKeyPath, cancellationToken);
                    else
                        sshPublicKey = await _sshKeyManager.EnsureKeyPairAsync(cancellationToken);

                    _logger.LogInformation("Sending SSH public key ({Length} chars) via KVP to VM {VMName}",
                        sshPublicKey?.Length ?? 0, vmSettings.VMName);
                    await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_SSH_PUBKEY", sshPublicKey, cancellationToken);

                    // ── Workflow flags ────────────────────────────────────────
#if DEBUG
                    //await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_DEBUG", "true", cancellationToken);
#endif
                    if (detectedGeneration == 2)
                    {
                        // Tell the ISO to run customize-only mode (skip disk cloning)
                        await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_MODE", "customize", cancellationToken);
                    }

                    if (vmCustomizations.ConfigureXrdp)
                    {
                        await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_XRDP", "true", cancellationToken);
                    }

                    // ── DNS nameservers ───────────────────────────────────────
                    string nameservers = vmCustomizations.DnsMode switch
                    {
                        DnsMode.Custom => vmCustomizations.CustomNameservers,
                        _ => ResolveHostDnsServers(),
                    };
                    if (!string.IsNullOrWhiteSpace(nameservers))
                    {
                        _logger.LogInformation("Sending DNS nameservers via KVP to VM {VMName}: {Nameservers}",
                            vmSettings.VMName, nameservers);
                        await _kvpSender.SendKVPToGuestAsync(vmSettings.VMName, "VMCREATE_NAMESERVERS", nameservers, cancellationToken);
                    }

                    // ── Monitor ISO progress and wait for shutdown ─────────────
                    const int shutdownTimeoutSeconds = 600;
                    bool shutDown;

                    if (detectedGeneration == 1)
                    {
                        // Gen1: Monitor partclone disk clone progress, then wait for shutdown.
                        // PollKVPForProgressAsync now has a timeout and VM-shutdown detection
                        // so we never hang indefinitely if the completion KVP is missed.
                        bool cloneMarkerSeen = await _kvpPoller.PollKVPForProgressAsync(
                            vmSettings.VMName, createVMProgressInfo, cancellationToken, shutdownTimeoutSeconds);

                        // Partclone done (or timed out) — transition from CloneDisk to Customize phase
                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "Customize" });

                        if (cloneMarkerSeen)
                        {
                            // Clone completed — poll WorkflowProgress KVP for pre-boot sub-step
                            // updates (Hyper-V packages, xRDP, PowerShell, SSH) while waiting
                            // for the remaining customization + shutdown.
                            shutDown = await _kvpPoller.WaitForShutdownWithProgressAsync(
                                vmSettings.VMName, createVMProgressInfo, cancellationToken, shutdownTimeoutSeconds);
                        }
                        else
                        {
                            // Timeout or VM already shut down during clone polling —
                            // check once with a 0s timeout to see current state.
                            shutDown = await _shutdownWatcher.WaitForVMShutdownAsync(vmSettings.VMName, cancellationToken, timeoutSeconds: 1);
                        }
                    }
                    else
                    {
                        // Gen2 customize-only: no partclone step.
                        // Poll WorkflowProgress KVP while waiting for the VM to shut
                        // itself down via OnSuccess=poweroff.target.
                        shutDown = await _kvpPoller.WaitForShutdownWithProgressAsync(
                            vmSettings.VMName, createVMProgressInfo, cancellationToken, shutdownTimeoutSeconds);
                    }

                    if (!shutDown)
                    {
                        _logger.LogWarning("VM {VMName} did not shut down within {Timeout}s — collecting diagnostics.", vmSettings.VMName, shutdownTimeoutSeconds);

                        // Collect diagnostics from the ISO guest via PowerShell Direct
                        var diagnostics = await _diagnosticsCollector
                            .CollectAsync(vmSettings.VMName, cancellationToken,
                                _sshKeyManager.GetPrivateKeyPath(vmCustomizations.CustomSshPublicKeyPath));

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
                        // Remove original disk from VM and delete the orphaned file
                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "Customize", SubStep = "Sub_CleanupIsoBoot" });
                        await _hyperVManager.RemoveHardDrive(vmSettings, OriginalDiskScsiControllerLocation, cancellationToken);

                        if (File.Exists(mediaPath))
                        {
                            File.Delete(mediaPath);
                            _logger.LogInformation("Deleted original MBR source disk: {MediaPath}", mediaPath);
                        }
                    }

                    // Remove ISO
                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "Customize", SubStep = "Sub_CleanupIsoBoot" });
                    await _hyperVManager.RemoveBootDvd(vmSettings, cloningIsoPath, cancellationToken);

                    // Set hard drive as first boot device now that DVD and old disk are removed
                    await _hyperVManager.SetFirstBootToHardDrive(vmSettings, cancellationToken);
                }
                if (vmCustomizations.EnableIntegrationServices)
                    await _hyperVManager.SetEnhancedSession(vmSettings, cancellationToken);

                // ── Post-boot: collect autorun log + run step pipeline ────
                var postBootSteps = _customizationSteps
                    .Where(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == StepPlatform.Linux && s.IsApplicable(item, vmCustomizations))
                    .OrderBy(s => s.Order)
                    .ToList();

                // After the ISO boot cycle the VM is off. Start it from the
                // hard drive so we can collect the autorun log and run any
                // post-boot customization steps.
                if (needsIsoBootCycle)
                {
                    if (postBootSteps.Count > 0)
                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "PostBoot" });

                    // Add a temporary second NIC on Default Switch so the host
                    // can reach the VM via DHCP even when the primary NIC uses a
                    // static IP that isn't routable on the Default Switch.
                    createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "PostBoot", SubStep = "Sub_AddTempNic" });
                    await _hyperVManager.RemoveTemporaryNetworkAdapter(vmSettings, cancellationToken); // idempotent cleanup
                    await _hyperVManager.AddTemporaryNetworkAdapter(vmSettings, cancellationToken);

                    try
                    {
                        await _hyperVManager.StartVM(vmSettings, cancellationToken);

                        string privateKeyPath = _sshKeyManager.GetPrivateKeyPath(vmCustomizations.CustomSshPublicKeyPath);
                        var shell = _guestShellFactory.Create(vmSettings.VMName, privateKeyPath);

                        createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "PostBoot", SubStep = "Sub_WaitForSsh" });
                        await shell.WaitForReadyAsync(cancellationToken);

                        // Collect the autorun log saved by the ISO's customize script
                        try
                        {
                            string autorunLog = await shell.RunCommandAsync(
                                "sudo cat /var/log/vmcreate-autorun.log 2>/dev/null || echo '[no autorun log found]'",
                                cancellationToken);
                            _logger.LogInformation("Autorun log for {VMName}:\n{Log}", vmSettings.VMName, autorunLog);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to collect autorun log (non-fatal)");
                        }

                        // Run post-boot customization steps
                        if (postBootSteps.Count > 0)
                        {
                            int completed = 0;
                            foreach (var step in postBootSteps)
                            {
                                _logger.LogInformation("Running post-boot step: {StepName} (order {Order})", step.Name, step.Order);
                                createVMProgressInfo.Report(new CreateVMProgressInfo
                                {
                                    Phase = "PostBoot",
                                    ProgressPercentage = (int)((double)completed / postBootSteps.Count * 100),
                                    StepName = step.Name
                                });

                                await step.ExecuteAsync(shell, item, vmCustomizations, _logger, cancellationToken);

                                completed++;
                                _logger.LogInformation("Completed post-boot step: {StepName}", step.Name);
                            }

                            createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "PostBoot", ProgressPercentage = 100 });
                        }
                    }
                    finally
                    {
                        // Always remove the temporary NIC — even if post-boot steps fail
                        try
                        {
                            await _hyperVManager.RemoveTemporaryNetworkAdapter(vmSettings, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to remove temporary network adapter (non-fatal)");
                        }
                    }

                    // VM is running from the hard drive with all customizations applied.
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("VM creation cancelled for {VMName} — cleaning up", vmSettings.VMName);
                if (!System.Diagnostics.Debugger.IsAttached)
                    await CleanupFailedVmAsync(vmSettings, vmCreated);
                else
                    _logger.LogWarning("Debug mode: skipping cleanup for {VMName} — VM preserved for investigation", vmSettings.VMName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating VM: {Message}", ex.Message);
                if (!System.Diagnostics.Debugger.IsAttached)
                    await CleanupFailedVmAsync(vmSettings, vmCreated);
                else
                    _logger.LogWarning("Debug mode: skipping cleanup for {VMName} — VM preserved for investigation", vmSettings.VMName);
                throw;
            }
        }

        /// <summary>
        /// Runs post-boot customization for a Windows VM over PowerShell Direct: waits for the
        /// guest to become reachable, then executes the applicable
        /// <see cref="CustomizationPhase.PostBoot"/> steps in order, reconnecting
        /// after any reboot a step triggers. Shared by both the Windows ISO and native-VHDX flows.
        /// No-op unless there is post-boot work to do.
        /// </summary>
        private async Task RunWindowsPostBootCustomizationAsync(
            VmSettings vmSettings, GalleryItem item, VmCustomizations vmCustomizations,
            IProgress<CreateVMProgressInfo> createVMProgressInfo, CancellationToken cancellationToken)
        {
            bool needsPostBoot = _customizationSteps
                .Any(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == StepPlatform.Windows && s.IsApplicable(item, vmCustomizations));

            if (!needsPostBoot)
                return;

            createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "PostBoot" });

            // Wait for the VM to finish setup/OOBE and become accessible.
            _logger.LogInformation("Waiting for Windows VM {VMName} to complete setup and become accessible...", vmSettings.VMName);
            var shell = _guestShellFactory.CreateForWindows(
                vmSettings.VMName,
                item.InitialUsername ?? "flare",
                item.InitialPassword ?? "flare");

            createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "PostBoot", SubStep = "Sub_WaitForSsh" });
            await shell.WaitForReadyAsync(cancellationToken);

            // Run post-boot customization steps
            var postBootSteps = _customizationSteps
                .Where(s => s.Phase == CustomizationPhase.PostBoot && s.Platform == StepPlatform.Windows && s.IsApplicable(item, vmCustomizations))
                .OrderBy(s => s.Order)
                .ToList();

            if (postBootSteps.Count == 0)
                return;

            int completed = 0;
            foreach (var step in postBootSteps)
            {
                _logger.LogInformation("Running post-boot step: {StepName} (order {Order})", step.Name, step.Order);
                createVMProgressInfo.Report(new CreateVMProgressInfo
                {
                    Phase = "PostBoot",
                    ProgressPercentage = (int)((double)completed / postBootSteps.Count * 100),
                    StepName = step.Name
                });

                await step.ExecuteAsync(shell, item, vmCustomizations, _logger, cancellationToken);

                // After each step, the VM may have rebooted. Re-establish the shell connection.
                try
                {
                    await shell.WaitForReadyAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "VM {VMName} may not be ready after step {StepName}, attempting to continue", vmSettings.VMName, step.Name);
                }

                completed++;
                _logger.LogInformation("Completed post-boot step: {StepName}", step.Name);
            }

            createVMProgressInfo.Report(new CreateVMProgressInfo { Phase = "PostBoot", ProgressPercentage = 100 });
        }

        /// <summary>
        /// Best-effort cleanup after a failed or cancelled deployment.
        /// Removes the VM from Hyper-V, deletes its VHDX files, and removes
        /// the VM configuration folder. Uses <see cref="CancellationToken.None"/>
        /// because the original token may already be cancelled.
        /// </summary>
        private async Task CleanupFailedVmAsync(VmSettings vmSettings, bool vmCreated)
        {
            string vmName = vmSettings.VMName;
            _logger.LogInformation("Cleaning up after failed/cancelled deployment: {VMName}", vmName);
            try
            {
                if (vmCreated)
                {
                    // Collect VHDX paths before removing the VM (Remove-VM detaches them)
                    string[] vhdxPaths;
                    try
                    {
                        vhdxPaths = await _hyperVManager.GetVmHardDiskPathsAsync(vmName, CancellationToken.None);
                    }
                    catch
                    {
                        vhdxPaths = Array.Empty<string>();
                    }

                    try { await _hyperVManager.StopVMAsync(vmName, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Stop VM during cleanup (may not be running)"); }

                    try { await _hyperVManager.RemoveVMAsync(vmName, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove VM {VMName} during cleanup", vmName); }

                    // Dismount any VHDX files that may still be mounted (e.g. from a
                    // failed UnattendInjector run) so we can delete them.
                    foreach (string vhdxPath in vhdxPaths)
                    {
                        TryDismountVhdx(vhdxPath);
                    }

                    foreach (string vhdxPath in vhdxPaths)
                    {
                        try
                        {
                            if (File.Exists(vhdxPath))
                            {
                                File.Delete(vhdxPath);
                                _logger.LogInformation("Cleanup: deleted VHDX {Path}", vhdxPath);
                            }
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "Cleanup: failed to delete {Path}", vhdxPath); }
                    }
                }

                // Delete the converted VHDX that may exist from PrepareMediaAsync
                // (not yet attached to a VM if vmCreated is false)
                string convertedVhdx = Path.Combine(_defaultVhdxPath, vmName + ".vhdx");
                TryDismountVhdx(convertedVhdx);
                try
                {
                    if (File.Exists(convertedVhdx))
                    {
                        File.Delete(convertedVhdx);
                        _logger.LogInformation("Cleanup: deleted converted VHDX {Path}", convertedVhdx);
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Cleanup: failed to delete {Path}", convertedVhdx); }

                // Remove the VM configuration folder Hyper-V creates
                string vmConfigFolder = Path.Combine(_defaultVmPath, vmName);
                try
                {
                    if (Directory.Exists(vmConfigFolder))
                    {
                        Directory.Delete(vmConfigFolder, true);
                        _logger.LogInformation("Cleanup: deleted VM config folder {Path}", vmConfigFolder);
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Cleanup: failed to delete folder {Path}", vmConfigFolder); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup encountered an unexpected error for {VMName}", vmName);
            }
        }



        /// <summary>
        /// Best-effort Dismount-VHD for a VHDX file. Used during cleanup to
        /// release files that may still be mounted from a failed UnattendInjector
        /// run. Errors are logged and swallowed — the VHDX may not be mounted.
        /// </summary>
        private void TryDismountVhdx(string vhdxPath)
        {
            try
            {
                using var ps = System.Management.Automation.PowerShell.Create();
                ps.AddCommand("Import-Module").AddParameter("Name", "Hyper-V").Invoke();
                ps.Commands.Clear();
                ps.AddCommand("Dismount-VHD").AddParameter("Path", vhdxPath);
                ps.Invoke();
                if (ps.HadErrors)
                    _logger.LogDebug("Dismount-VHD reported errors (VHDX may not have been mounted): {Error}",
                        string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
                else
                    _logger.LogInformation("Dismounted VHDX during cleanup: {VhdxPath}", vhdxPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dismount-VHD threw (VHDX may not have been mounted)");
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
                string vmConfigFolder = Path.Combine(_defaultVmPath, existingVmName);
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