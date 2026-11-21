using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VMCreate.Manager.HyperV
{
    /// <summary>
    /// <see cref="IHyperVHost"/> over WMI (root\virtualization\v2). Works
    /// without elevation for members of "Hyper-V Administrators". WMI is used
    /// instead of the Hyper-V PowerShell module: enumeration is much faster,
    /// it offers change events, and it avoids hosting a PowerShell runspace.
    /// </summary>
    public sealed class WmiHyperVHost : IHyperVHost
    {
        private const string Namespace = @"root\virtualization\v2";
        private const uint ReturnOk = 0;
        private const uint ReturnJobStarted = 4096;

        // GetSummaryInformation RequestedInformation codes (Msvm_SummaryInformation).
        private static readonly uint[] SummaryCodes =
        {
            0,   // Name (VM GUID)
            4,   // NumberOfProcessors
            101, // ProcessorLoad
            103, // MemoryUsage (MB)
            104, // Heartbeat
            106, // GuestOperatingSystem
        };

        /// <summary>The Default Switch has a fixed ID; it NATs (ICS) to the host's network.</summary>
        internal static readonly Guid DefaultSwitchId = Guid.Parse("c08cb7b8-9b3c-408e-8e30-5e16a3aeb444");

        private const ushort PortEnabled = 2;
        private const ushort PortDisabled = 3;

        private static readonly Regex HostResourceName = new(@"Name=""(?<id>[0-9A-Fa-f-]{36})""", RegexOptions.CultureInvariant);

        private readonly ILogger<WmiHyperVHost> _logger;
        private readonly string _machine;

        public WmiHyperVHost(ILogger<WmiHyperVHost>? logger = null, string machine = ".")
        {
            _logger = logger ?? NullLogger<WmiHyperVHost>.Instance;
            _machine = machine;
        }

        public Task<HostSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.Run(() => ReadSnapshot(cancellationToken), cancellationToken);

        private HostSnapshot ReadSnapshot(CancellationToken ct)
        {
            var scope = Connect();

            var computerSystems = new Dictionary<Guid, (string Name, ushort State, ulong OnTime)>();
            foreach (var mo in Query(scope, "SELECT Name, ElementName, EnabledState, OnTimeInMilliseconds FROM Msvm_ComputerSystem"))
            {
                // The host itself is also an Msvm_ComputerSystem, named after the
                // machine. Caption/Description are localized, so tell VMs apart
                // by their GUID name instead.
                if (!Guid.TryParse(mo["Name"] as string, out var id))
                    continue;
                computerSystems[id] = (mo["ElementName"] as string ?? "", ToUShort(mo["EnabledState"]), ToULong(mo["OnTimeInMilliseconds"]));
            }
            ct.ThrowIfCancellationRequested();

            var vms = new List<RawVm>();
            var settingPaths = new List<string>();
            foreach (var mo in Query(scope, "SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'"))
            {
                if (!Guid.TryParse(mo["VirtualSystemIdentifier"] as string, out var id) || !computerSystems.TryGetValue(id, out var cs))
                    continue;
                settingPaths.Add(((ManagementObject)mo).Path.Path);
                vms.Add(new RawVm(
                    id,
                    cs.Name,
                    cs.State,
                    cs.OnTime,
                    ParseGeneration(mo["VirtualSystemSubType"] as string),
                    mo["ConfigurationDataRoot"] as string,
                    JoinNotes(mo["Notes"]),
                    ToDateTime(mo["CreationTime"])));
            }
            ct.ThrowIfCancellationRequested();

            var checkpoints = new List<RawCheckpoint>();
            foreach (var mo in Query(scope, "SELECT VirtualSystemIdentifier, ElementName FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:Snapshot:Realized'"))
            {
                if (Guid.TryParse(mo["VirtualSystemIdentifier"] as string, out var id))
                    checkpoints.Add(new RawCheckpoint(id, mo["ElementName"] as string ?? ""));
            }

            var summaries = ReadSummaries(scope, settingPaths);
            ct.ThrowIfCancellationRequested();

            var adapters = new List<RawGuestNetworkAdapter>();
            foreach (var mo in Query(scope, "SELECT InstanceID, IPAddresses FROM Msvm_GuestNetworkAdapterConfiguration"))
            {
                if (TryParseGuestNetworkVmId(mo["InstanceID"] as string, out var vmId))
                    adapters.Add(new RawGuestNetworkAdapter(vmId, (mo["IPAddresses"] as string[]) ?? Array.Empty<string>()));
            }

            var switchNames = new Dictionary<Guid, string>();
            foreach (var mo in Query(scope, "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch"))
            {
                if (Guid.TryParse(mo["Name"] as string, out var id))
                    switchNames[id] = mo["ElementName"] as string ?? "";
            }

            // Port allocations are "Microsoft:{owner}\{port}...": the owner is a VM for VM
            // adapters, or a switch for the switch's own host-side ports, whose
            // HostResource tells the switch type (physical NIC = external, host = internal).
            var ports = new List<RawSwitchPort>();
            var switchKinds = new Dictionary<Guid, SwitchKind>();
            foreach (var mo in Query(scope, "SELECT InstanceID, HostResource, EnabledState FROM Msvm_EthernetPortAllocationSettingData"))
            {
                if (!TryParseSettingVmId(mo["InstanceID"] as string, out var ownerId))
                    continue;
                var hostResource = mo["HostResource"] as string[];
                if (computerSystems.ContainsKey(ownerId))
                    ports.Add(new RawSwitchPort(ownerId, ParseHostResourceSwitch(hostResource), ToUShort(mo["EnabledState"]) != PortDisabled));
                else if (switchNames.ContainsKey(ownerId) && ClassifyHostSidePort(hostResource) is SwitchKind kind
                         && (!switchKinds.TryGetValue(ownerId, out var known) || kind > known))
                    switchKinds[ownerId] = kind;
            }

            var natSwitches = ReadNatSwitchNames();
            var switches = switchNames.Select(kv =>
            {
                var kind = switchKinds.GetValueOrDefault(kv.Key, SwitchKind.Private);
                bool nat = kind == SwitchKind.Internal && (kv.Key == DefaultSwitchId || natSwitches.Contains(kv.Value));
                return new RawSwitch(kv.Key, kv.Value, kind, nat);
            }).ToList();

            var memory = new List<RawMemorySetting>();
            foreach (var mo in Query(scope, "SELECT InstanceID, VirtualQuantity, DynamicMemoryEnabled FROM Msvm_MemorySettingData"))
            {
                // Snapshot and definition settings carry other IDs; keep the realized VM ones.
                if (TryParseSettingVmId(mo["InstanceID"] as string, out var vmId) && computerSystems.ContainsKey(vmId))
                    memory.Add(new RawMemorySetting(vmId, ToULong(mo["VirtualQuantity"]), mo["DynamicMemoryEnabled"] is true));
            }

            return new HostSnapshot(vms, summaries, adapters, ports, switches, memory, checkpoints);
        }

        private List<RawVmSummary> ReadSummaries(ManagementScope scope, List<string> settingPaths)
        {
            var result = new List<RawVmSummary>();
            if (settingPaths.Count == 0)
                return result;

            using var service = GetManagementService(scope);
            using var inParams = service.GetMethodParameters("GetSummaryInformation");
            inParams["SettingData"] = settingPaths.ToArray();
            inParams["RequestedInformation"] = SummaryCodes;
            using var outParams = service.InvokeMethod("GetSummaryInformation", inParams, null);
            if (ToUInt(outParams["ReturnValue"]) != ReturnOk || outParams["SummaryInformation"] is not ManagementBaseObject[] infos)
            {
                _logger.LogWarning("GetSummaryInformation returned {ReturnValue}", outParams["ReturnValue"]);
                return result;
            }

            foreach (var info in infos)
            {
                if (!Guid.TryParse(info["Name"] as string, out var id))
                    continue;
                result.Add(new RawVmSummary(
                    id,
                    info["NumberOfProcessors"] is null ? 0 : ToUShort(info["NumberOfProcessors"]),
                    info["ProcessorLoad"] is null ? null : ToUShort(info["ProcessorLoad"]),
                    info["MemoryUsage"] is null ? null : ToULong(info["MemoryUsage"]),
                    info["Heartbeat"] is null ? null : ToUShort(info["Heartbeat"]),
                    info["GuestOperatingSystem"] as string));
            }
            return result;
        }

        public Task RequestStateChangeAsync(Guid vmId, RequestedVmState state, CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                var scope = Connect();
                using var vm = GetComputerSystem(scope, vmId);
                using var inParams = vm.GetMethodParameters("RequestStateChange");
                inParams["RequestedState"] = (ushort)state;
                using var outParams = vm.InvokeMethod("RequestStateChange", inParams, null);
                CompleteOperation(scope, outParams, $"change state of VM {vmId} to {state}", cancellationToken);
            }, cancellationToken);

        public Task ShutdownAsync(Guid vmId, bool force, CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                var scope = Connect();
                using var component = QueryFirst(scope, $"SELECT * FROM Msvm_ShutdownComponent WHERE SystemName = '{vmId:D}'")
                    ?? throw new HyperVOperationException("The Shutdown integration service is not available for this VM (VM off, or guest services not running).");
                using var inParams = component.GetMethodParameters("InitiateShutdown");
                inParams["Force"] = force;
                inParams["Reason"] = "Shut down by VMCreate Manager";
                using var outParams = component.InvokeMethod("InitiateShutdown", inParams, null);
                CompleteOperation(scope, outParams, $"shut down VM {vmId}", cancellationToken);
            }, cancellationToken);

        public Task<string> GetNotesAsync(Guid vmId, CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                var scope = Connect();
                using var settings = GetRealizedSettings(scope, vmId);
                return JoinNotes(settings["Notes"]);
            }, cancellationToken);

        public Task SetNotesAsync(Guid vmId, string notes, CancellationToken cancellationToken = default)
            => ModifySettingsAsync(vmId, s => s["Notes"] = new[] { notes }, "update notes", cancellationToken);

        public Task RenameAsync(Guid vmId, string newName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("VM name cannot be empty.", nameof(newName));
            return ModifySettingsAsync(vmId, s => s["ElementName"] = newName.Trim(), "rename", cancellationToken);
        }

        public Task<VmThumbnail?> GetThumbnailAsync(Guid vmId, int maxWidth, int maxHeight, CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                var scope = Connect();

                // Scale to the guest's current resolution so a 16:9 desktop isn't squashed into 4:3.
                int guestWidth = 4, guestHeight = 3;
                using (var head = QueryFirst(scope, $"SELECT CurrentHorizontalResolution, CurrentVerticalResolution FROM Msvm_VideoHead WHERE SystemName = '{vmId:D}'"))
                {
                    if (head != null && ToUInt(head["CurrentHorizontalResolution"]) is uint w and > 0 && ToUInt(head["CurrentVerticalResolution"]) is uint h and > 0)
                    {
                        guestWidth = (int)w;
                        guestHeight = (int)h;
                    }
                }
                var (width, height) = FitThumbnail(guestWidth, guestHeight, maxWidth, maxHeight);

                using var settings = GetRealizedSettings(scope, vmId);
                using var service = GetManagementService(scope);
                using var inParams = service.GetMethodParameters("GetVirtualSystemThumbnailImage");
                inParams["TargetSystem"] = settings.Path.Path;
                inParams["WidthPixels"] = (ushort)width;
                inParams["HeightPixels"] = (ushort)height;
                using var outParams = service.InvokeMethod("GetVirtualSystemThumbnailImage", inParams, null);

                uint rc = ToUInt(outParams["ReturnValue"]);
                if (rc != ReturnOk || outParams["ImageData"] is not byte[] data)
                    return null; // 32775 = VM off / no display: not an error for a preview
                return ToThumbnail(width, height, data);
            }, cancellationToken);

        /// <summary>
        /// Wraps the raw buffer. Hyper-V returns 4 trailing bytes beyond
        /// width*height*2 (verified against a live guest: offset 0 renders
        /// correctly, offset 4 shifts the image left).
        /// </summary>
        internal static VmThumbnail? ToThumbnail(int width, int height, byte[] data)
        {
            int expected = width * height * 2;
            if (data.Length < expected)
                return null;
            return new VmThumbnail(width, height, data.Length == expected ? data : data.AsSpan(0, expected).ToArray());
        }

        internal static (int Width, int Height) FitThumbnail(int guestWidth, int guestHeight, int maxWidth, int maxHeight)
        {
            double scale = Math.Min((double)maxWidth / guestWidth, (double)maxHeight / guestHeight);
            int width = Math.Max(1, (int)Math.Round(guestWidth * scale));
            int height = Math.Max(1, (int)Math.Round(guestHeight * scale));
            return (width, height);
        }

        public Task<IReadOnlyList<string>> DisconnectNetworkAsync(Guid vmId, CancellationToken cancellationToken = default)
            => Task.Run<IReadOnlyList<string>>(() =>
            {
                var scope = Connect();
                var ports = GetVmPorts(scope, vmId).Where(p => ToUShort(p["EnabledState"]) != PortDisabled).ToList();
                foreach (var port in ports)
                    port["EnabledState"] = PortDisabled;
                ModifyResources(scope, ports, $"disconnect the network of VM {vmId}", cancellationToken);
                return ports.Select(p => (string)p["InstanceID"]).ToList();
            }, cancellationToken);

        public Task<int> ReconnectNetworkAsync(Guid vmId, IReadOnlyCollection<string>? portIds, CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                var scope = Connect();
                var ports = GetVmPorts(scope, vmId)
                    .Where(p => ToUShort(p["EnabledState"]) == PortDisabled
                                && ParseHostResourceSwitch(p["HostResource"] as string[]) != null
                                && (portIds == null || portIds.Contains((string)p["InstanceID"], StringComparer.OrdinalIgnoreCase)))
                    .ToList();
                foreach (var port in ports)
                    port["EnabledState"] = PortEnabled;
                ModifyResources(scope, ports, $"reconnect the network of VM {vmId}", cancellationToken);
                return ports.Count;
            }, cancellationToken);

        public Task<int> ConnectNetworkToSwitchAsync(Guid vmId, Guid switchId, CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                var scope = Connect();
                using var vswitch = QueryFirst(scope, $"SELECT * FROM Msvm_VirtualEthernetSwitch WHERE Name = '{switchId:D}'")
                    ?? throw new HyperVOperationException($"Switch {switchId} no longer exists.");
                var ports = GetVmPorts(scope, vmId);
                if (ports.Count == 0)
                    throw new HyperVOperationException("The VM has no network adapter.");
                foreach (var port in ports)
                {
                    port["HostResource"] = new[] { vswitch.Path.Path };
                    port["EnabledState"] = PortEnabled;
                }
                ModifyResources(scope, ports, $"connect VM {vmId} to switch '{vswitch["ElementName"]}'", cancellationToken);
                return ports.Count;
            }, cancellationToken);

        public Task<string> GetDefaultVmFolderAsync(CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                var scope = Connect();
                using var settings = QueryFirst(scope, "SELECT DefaultExternalDataRoot FROM Msvm_VirtualSystemManagementServiceSettingData")
                    ?? throw new HyperVOperationException("Hyper-V host settings not found.");
                return settings["DefaultExternalDataRoot"] as string is { Length: > 0 } root
                    ? root
                    : throw new HyperVOperationException("The Hyper-V host has no default virtual machine folder.");
            }, cancellationToken);

        private const string VirtualHardDiskSubType = "Microsoft:Hyper-V:Virtual Hard Disk";

        public Task<Guid> CreateLinkedCloneAsync(Guid templateId, string cloneName, string folder, string notes, CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                if (Directory.Exists(folder) || File.Exists(folder))
                    throw new HyperVOperationException($"'{folder}' already exists; pick another name.");

                var scope = Connect();
                using var template = GetComputerSystem(scope, templateId);
                if (ToUShort(template["EnabledState"]) != (ushort)RequestedVmState.Off)
                    throw new HyperVOperationException("The template must be turned off: linked clones share its disks.");
                // Hyper-V refuses a checkpoint disk (.avhdx) as a parent; say why before creating anything.
                using (var templateSettings = GetRealizedSettings(scope, templateId))
                {
                    if (templateSettings.GetRelated("Msvm_StorageAllocationSettingData").Cast<ManagementObject>()
                        .Any(d => (d["HostResource"] as string[])?.FirstOrDefault() is string disk
                                  && Path.GetExtension(disk).StartsWith(".avhd", StringComparison.OrdinalIgnoreCase)))
                        throw new HyperVOperationException("The template has checkpoints. Delete or apply them first: clones need its final disks as parents.");
                }

                using var service = GetManagementService(scope);
                string exportDir = Path.Combine(folder, ".export");
                var createdDisks = new List<string>();
                ManagementObject? planned = null;
                try
                {
                    // 1. The template's configuration only; its disks stay where they are.
                    Directory.CreateDirectory(exportDir);
                    using (var cls = new ManagementClass(scope, new ManagementPath("Msvm_VirtualSystemExportSettingData"), null))
                    using (var exportSettings = cls.CreateInstance())
                    {
                        exportSettings["CopySnapshotConfiguration"] = (byte)1; // no checkpoints
                        exportSettings["CopyVmRuntimeInformation"] = false;
                        exportSettings["CopyVmStorage"] = false;
                        exportSettings["CreateVmExportSubdirectory"] = false;
                        using var p = service.GetMethodParameters("ExportSystemDefinition");
                        p["ComputerSystem"] = template.Path.Path;
                        p["ExportDirectory"] = exportDir;
                        p["ExportSettingData"] = exportSettings.GetText(TextFormat.CimDtd20);
                        using var o = service.InvokeMethod("ExportSystemDefinition", p, null);
                        CompleteOperation(scope, o, "export the template configuration", cancellationToken);
                    }

                    // 2. Import it as a planned VM with a new ID (and so new dynamic MACs).
                    string vmcx = Directory.GetFiles(exportDir, "*.vmcx", SearchOption.AllDirectories).Single();
                    using (var p = service.GetMethodParameters("ImportSystemDefinition"))
                    {
                        p["SystemDefinitionFile"] = vmcx;
                        p["SnapshotFolder"] = Path.Combine(folder, "Snapshots");
                        p["GenerateNewSystemIdentifier"] = true;
                        using var o = service.InvokeMethod("ImportSystemDefinition", p, null);
                        CompleteOperation(scope, o, "import the clone configuration", cancellationToken);
                        planned = new ManagementObject(scope, new ManagementPath((string)o["ImportedSystem"]), null);
                        planned.Get();
                    }
                    var cloneId = Guid.Parse((string)planned["Name"]);

                    // 3. Name, notes, and keep the configuration inside the clone's folder.
                    using (var settings = planned.GetRelated("Msvm_VirtualSystemSettingData").Cast<ManagementObject>().Single())
                    {
                        settings["ElementName"] = cloneName;
                        settings["Notes"] = new[] { notes };
                        settings["ConfigurationDataRoot"] = folder;
                        settings["SnapshotDataRoot"] = folder;
                        settings["SwapFileDataRoot"] = folder;
                        using var p = service.GetMethodParameters("ModifySystemSettings");
                        p["SystemSettings"] = settings.GetText(TextFormat.CimDtd20);
                        using var o = service.InvokeMethod("ModifySystemSettings", p, null);
                        CompleteOperation(scope, o, "configure the clone", cancellationToken);

                        // 4. A differencing disk per virtual hard disk; ISOs stay shared.
                        string diskDir = Path.Combine(folder, "Virtual Hard Disks");
                        Directory.CreateDirectory(diskDir);
                        using var images = QueryFirst(scope, "SELECT * FROM Msvm_ImageManagementService")
                            ?? throw new HyperVOperationException("Msvm_ImageManagementService not found.");
                        var disks = settings.GetRelated("Msvm_StorageAllocationSettingData").Cast<ManagementObject>()
                            .Where(d => d["ResourceSubType"] as string == VirtualHardDiskSubType && (d["HostResource"] as string[])?.Length > 0)
                            .ToList();
                        foreach (var disk in disks)
                        {
                            string parent = ((string[])disk["HostResource"])[0];
                            string child = UniquePath(diskDir, $"{Path.GetFileNameWithoutExtension(parent)}-{cloneName}.vhdx");
                            CreateDifferencingDisk(scope, images, parent, child, cancellationToken);
                            createdDisks.Add(child);
                            disk["HostResource"] = new[] { child };
                        }
                        ModifyResources(scope, disks, "attach the clone's disks", cancellationToken);
                    }

                    // 5. Make it a real VM.
                    foreach (string method in new[] { "ValidatePlannedSystem", "RealizePlannedSystem" })
                    {
                        using var p = service.GetMethodParameters(method);
                        p["PlannedSystem"] = planned.Path.Path;
                        using var o = service.InvokeMethod(method, p, null);
                        CompleteOperation(scope, o, method == "ValidatePlannedSystem" ? "validate the clone" : "create the clone", cancellationToken);
                    }
                    planned = null; // realized: no longer ours to roll back
                    TryDeleteDirectory(exportDir);
                    _logger.LogInformation("Created linked clone {Clone} ({CloneId}) of {Template}", cloneName, cloneId, templateId);
                    return cloneId;
                }
                catch
                {
                    RollBackClone(scope, service, planned, createdDisks, folder);
                    throw;
                }
            }, cancellationToken);

        private static void CreateDifferencingDisk(ManagementScope scope, ManagementObject images, string parent, string child, CancellationToken ct)
        {
            using var cls = new ManagementClass(scope, new ManagementPath("Msvm_VirtualHardDiskSettingData"), null);
            using var vhd = cls.CreateInstance();
            vhd["Type"] = (ushort)4;   // Differencing
            vhd["Format"] = (ushort)3; // VHDX
            vhd["Path"] = child;
            vhd["ParentPath"] = parent;
            using var p = images.GetMethodParameters("CreateVirtualHardDisk");
            p["VirtualDiskSettingData"] = vhd.GetText(TextFormat.CimDtd20);
            using var o = images.InvokeMethod("CreateVirtualHardDisk", p, null);
            CompleteOperation(scope, o, $"create differencing disk '{child}'", ct);
        }

        /// <summary>Removes what a failed clone left: the planned VM, the disks we created, our (new) folder.</summary>
        private void RollBackClone(ManagementScope scope, ManagementObject service, ManagementObject? planned, List<string> createdDisks, string folder)
        {
            try
            {
                if (planned != null)
                {
                    using var p = service.GetMethodParameters("DestroySystem");
                    p["AffectedSystem"] = planned.Path.Path;
                    using var o = service.InvokeMethod("DestroySystem", p, null);
                    CompleteOperation(scope, o, "remove the planned clone", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove the planned clone");
            }
            foreach (string disk in createdDisks)
            {
                try { File.Delete(disk); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Could not remove {Disk}", disk); }
            }
            // The folder did not exist before (checked up front), so everything in it is ours.
            TryDeleteDirectory(folder);
        }

        private void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove {Dir}", dir);
            }
        }

        private static string UniquePath(string dir, string fileName)
        {
            string path = Path.Combine(dir, fileName);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(fileName)} ({i}){Path.GetExtension(fileName)}");
            return path;
        }

        /// <summary>The VM's network adapter port allocations (one per adapter).</summary>
        private static List<ManagementObject> GetVmPorts(ManagementScope scope, Guid vmId)
            => Query(scope, "SELECT * FROM Msvm_EthernetPortAllocationSettingData")
                .Where(mo => TryParseSettingVmId(mo["InstanceID"] as string, out var owner) && owner == vmId)
                .Select(mo => new ManagementObject(scope, ((ManagementObject)mo).Path, null))
                .ToList();

        private static void ModifyResources(ManagementScope scope, List<ManagementObject> resources, string what, CancellationToken ct)
        {
            if (resources.Count == 0)
                return;
            using var service = GetManagementService(scope);
            using var inParams = service.GetMethodParameters("ModifyResourceSettings");
            inParams["ResourceSettings"] = resources.Select(r => r.GetText(TextFormat.CimDtd20)).ToArray();
            using var outParams = service.InvokeMethod("ModifyResourceSettings", inParams, null);
            CompleteOperation(scope, outParams, what, ct);
        }

        /// <summary>
        /// Names of switches whose host vNIC ("vEthernet (name)") sits in a NetNat
        /// internal prefix. Best effort: NAT state is a hint, never worth failing a refresh.
        /// </summary>
        private HashSet<string> ReadNatSwitchNames()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var scope = new ManagementScope($@"\\{_machine}\root\StandardCimv2");
                scope.Connect();
                var prefixes = Query(scope, "SELECT InternalIPInterfaceAddressPrefix FROM MSFT_NetNat")
                    .Select(mo => mo["InternalIPInterfaceAddressPrefix"] as string)
                    .OfType<string>().Where(p => p.Length > 0).ToList();
                if (prefixes.Count == 0)
                    return result;
                const string aliasPrefix = "vEthernet (";
                foreach (var mo in Query(scope, "SELECT InterfaceAlias, IPAddress FROM MSFT_NetIPAddress WHERE AddressFamily = 2"))
                {
                    if (mo["InterfaceAlias"] is string alias && alias.StartsWith(aliasPrefix, StringComparison.OrdinalIgnoreCase) && alias.EndsWith(')')
                        && mo["IPAddress"] is string ip && prefixes.Any(p => IsInPrefix(ip, p)))
                        result.Add(alias[aliasPrefix.Length..^1]);
                }
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                _logger.LogDebug(ex, "Could not read NAT configuration");
            }
            return result;
        }

        private Task ModifySettingsAsync(Guid vmId, Action<ManagementObject> change, string what, CancellationToken cancellationToken)
            => Task.Run(() =>
            {
                var scope = Connect();
                using var settings = GetRealizedSettings(scope, vmId);
                change(settings);
                using var service = GetManagementService(scope);
                using var inParams = service.GetMethodParameters("ModifySystemSettings");
                inParams["SystemSettings"] = settings.GetText(TextFormat.CimDtd20);
                using var outParams = service.InvokeMethod("ModifySystemSettings", inParams, null);
                CompleteOperation(scope, outParams, $"{what} of VM {vmId}", cancellationToken);
            }, cancellationToken);

        public IDisposable WatchChanges(Action onChange)
        {
            var scope = Connect();
            var watchers = new List<ManagementEventWatcher>();
            foreach (string cls in new[] { "Msvm_ComputerSystem", "Msvm_VirtualSystemSettingData" })
            {
                var watcher = new ManagementEventWatcher(scope,
                    new WqlEventQuery($"SELECT * FROM __InstanceOperationEvent WITHIN 2 WHERE TargetInstance ISA '{cls}'"));
                watcher.EventArrived += (_, e) =>
                {
                    e.NewEvent.Dispose();
                    try { onChange(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Change handler failed"); }
                };
                watcher.Start();
                watchers.Add(watcher);
            }
            return new WatcherSet(watchers);
        }

        private sealed class WatcherSet : IDisposable
        {
            private readonly List<ManagementEventWatcher> _watchers;
            public WatcherSet(List<ManagementEventWatcher> watchers) => _watchers = watchers;

            public void Dispose()
            {
                foreach (var w in _watchers)
                {
                    try { w.Stop(); } catch (ManagementException) { }
                    w.Dispose();
                }
                _watchers.Clear();
            }
        }

        // ── storage graph and deletion ─────────────────────────────────────

        private static readonly Regex CimProperty = new(@"<PROPERTY NAME=""(?<name>ParentPath|Type)""[^>]*>\s*<VALUE>(?<value>[^<]*)</VALUE>", RegexOptions.CultureInvariant);

        public Task<StorageGraph> ReadStorageGraphAsync(CancellationToken cancellationToken = default)
            => Task.Run(() => ReadStorageGraph(cancellationToken), cancellationToken);

        private StorageGraph ReadStorageGraph(CancellationToken ct)
        {
            var scope = Connect();

            // Settings owners: a realized VM ("Microsoft:{vm}") or a checkpoint
            // ("Microsoft:{checkpoint}", whose VirtualSystemIdentifier is the VM).
            var owners = new Dictionary<Guid, (Guid VmId, string? Checkpoint)>();
            var roots = new List<RawVmStorageRoots>();
            var systemIds = new HashSet<Guid>();
            var checkpoints = new List<RawCheckpointInfo>();
            foreach (var mo in Query(scope, "SELECT InstanceID, VirtualSystemIdentifier, VirtualSystemType, ElementName, ConfigurationDataRoot, SnapshotDataRoot, SwapFileDataRoot, CreationTime FROM Msvm_VirtualSystemSettingData"))
            {
                if (!TryParseSettingVmId(mo["InstanceID"] as string, out var settingId) || !Guid.TryParse(mo["VirtualSystemIdentifier"] as string, out var vmId))
                    continue;
                // Realized, planned and checkpoint settings alike: their files are in use.
                systemIds.Add(settingId);
                systemIds.Add(vmId);
                switch (mo["VirtualSystemType"] as string)
                {
                    case "Microsoft:Hyper-V:System:Realized":
                        owners[settingId] = (vmId, null);
                        roots.Add(new RawVmStorageRoots(vmId, mo["ConfigurationDataRoot"] as string, mo["SnapshotDataRoot"] as string, mo["SwapFileDataRoot"] as string));
                        break;
                    case "Microsoft:Hyper-V:Snapshot:Realized":
                        owners[settingId] = (vmId, mo["ElementName"] as string ?? "");
                        checkpoints.Add(new RawCheckpointInfo(vmId, settingId, mo["ElementName"] as string ?? "", ToDateTime(mo["CreationTime"])));
                        break;
                }
            }
            ct.ThrowIfCancellationRequested();

            var attachments = new List<RawAttachment>();
            foreach (var mo in Query(scope, "SELECT InstanceID, HostResource, ResourceSubType FROM Msvm_StorageAllocationSettingData"))
            {
                if (mo["HostResource"] is not string[] { Length: > 0 } hostResource || string.IsNullOrWhiteSpace(hostResource[0]))
                    continue;
                AttachedMediaKind? kind = (mo["ResourceSubType"] as string) switch
                {
                    VirtualHardDiskSubType => AttachedMediaKind.VirtualHardDisk,
                    "Microsoft:Hyper-V:Virtual CD/DVD Disk" => AttachedMediaKind.Iso,
                    "Microsoft:Hyper-V:Virtual Floppy Disk" => AttachedMediaKind.Floppy,
                    _ => null,
                };
                if (kind == null)
                    continue;
                (Guid VmId, string? Checkpoint)? owner = TryParseSettingVmId(mo["InstanceID"] as string, out var settingId) && owners.TryGetValue(settingId, out var o) ? o : null;
                attachments.Add(new RawAttachment(owner?.VmId, owner?.Checkpoint, NormalizePath(hostResource[0]), kind.Value));
            }
            ct.ThrowIfCancellationRequested();

            // Walk each virtual disk's parent chain once.
            var files = new Dictionary<string, RawStorageFile>(StringComparer.OrdinalIgnoreCase);
            using var images = QueryFirst(scope, "SELECT * FROM Msvm_ImageManagementService")
                ?? throw new HyperVOperationException("Msvm_ImageManagementService not found.");
            var pending = new Queue<string>(attachments.Where(a => a.Kind == AttachedMediaKind.VirtualHardDisk).Select(a => a.Path));
            while (pending.TryDequeue(out var path))
            {
                if (files.ContainsKey(path))
                    continue;
                ct.ThrowIfCancellationRequested();
                var disk = ReadVirtualDisk(scope, images, path);
                files[path] = disk;
                if (disk.ParentPath != null)
                    pending.Enqueue(disk.ParentPath);
            }
            foreach (var media in attachments.Where(a => a.Kind != AttachedMediaKind.VirtualHardDisk))
            {
                if (!files.ContainsKey(media.Path))
                    files[media.Path] = new RawStorageFile(media.Path, File.Exists(media.Path), 0, null, TryGetFileSize(media.Path));
            }

            using var hostSettings = QueryFirst(scope, "SELECT DefaultExternalDataRoot, DefaultVirtualHardDiskPath FROM Msvm_VirtualSystemManagementServiceSettingData");
            var hostRoots = new[] { hostSettings?["DefaultExternalDataRoot"] as string, hostSettings?["DefaultVirtualHardDiskPath"] as string }
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => NormalizePath(r!))
                .ToList();

            return new StorageGraph(attachments, files, roots, hostRoots, systemIds, checkpoints);
        }

        public Task<IReadOnlyList<RawStorageFile>> InspectVirtualDisksAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
            => Task.Run<IReadOnlyList<RawStorageFile>>(() =>
            {
                var scope = Connect();
                using var images = QueryFirst(scope, "SELECT * FROM Msvm_ImageManagementService")
                    ?? throw new HyperVOperationException("Msvm_ImageManagementService not found.");
                var result = new List<RawStorageFile>(paths.Count);
                foreach (string path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Add(ReadVirtualDisk(scope, images, NormalizePath(path)));
                }
                return result;
            }, cancellationToken);

        private static long? TryGetFileSize(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// The image service runs as the host, so it reads disks this process may
        /// not be allowed to open. A failed call (or failed job) means the file is
        /// missing or broken: then nothing is known about its parent.
        /// </summary>
        private RawStorageFile ReadVirtualDisk(ManagementScope scope, ManagementObject images, string path)
        {
            long? size = TryGetFileSize(path);
            try
            {
                using var p = images.GetMethodParameters("GetVirtualHardDiskSettingData");
                p["Path"] = path;
                using var o = images.InvokeMethod("GetVirtualHardDiskSettingData", p, null);
                // The settings come back only on a synchronous success; a job here
                // is how the service reports an unreadable file.
                if (ToUInt(o["ReturnValue"]) != ReturnOk || o["SettingData"] is not string xml)
                {
                    if (ToUInt(o["ReturnValue"]) == ReturnJobStarted)
                    {
                        try { CompleteOperation(scope, o, $"read '{path}'", CancellationToken.None); }
                        catch (HyperVOperationException ex) { _logger.LogInformation("Virtual disk {Path} is unreadable: {Error}", path, ex.Message); }
                    }
                    return new RawStorageFile(path, false, 0, null, size);
                }
                var (type, parent) = ParseVirtualDiskSettings(xml);
                return new RawStorageFile(path, true, type, parent == null ? null : NormalizePath(parent), size);
            }
            catch (ManagementException ex)
            {
                _logger.LogWarning(ex, "Could not read virtual disk {Path}", path);
                return new RawStorageFile(path, false, 0, null, size);
            }
        }

        /// <summary>Type and ParentPath from a CIM-XML Msvm_VirtualHardDiskSettingData.</summary>
        internal static (ushort Type, string? ParentPath) ParseVirtualDiskSettings(string xml)
        {
            ushort type = 0;
            string? parent = null;
            foreach (Match m in CimProperty.Matches(xml))
            {
                string value = System.Net.WebUtility.HtmlDecode(m.Groups["value"].Value).Trim();
                if (m.Groups["name"].Value == "Type")
                    ushort.TryParse(value, out type);
                else if (value.Length > 0)
                    parent = value;
            }
            return (type, parent);
        }

        internal static string NormalizePath(string path)
        {
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        }

        public Task DestroyVmAsync(Guid vmId, CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                var scope = Connect();
                using var vm = GetComputerSystem(scope, vmId);
                using var service = GetManagementService(scope);
                using var p = service.GetMethodParameters("DestroySystem");
                p["AffectedSystem"] = vm.Path.Path;
                using var o = service.InvokeMethod("DestroySystem", p, null);
                CompleteOperation(scope, o, "remove the VM from Hyper-V", cancellationToken);
                _logger.LogInformation("Destroyed VM {VmId}", vmId);
            }, cancellationToken);

        public Task ExportVmAsync(Guid vmId, string directory, CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                    throw new HyperVOperationException($"'{directory}' is not empty.");
                Directory.CreateDirectory(directory);

                var scope = Connect();
                using var vm = GetComputerSystem(scope, vmId);
                using var service = GetManagementService(scope);
                using var cls = new ManagementClass(scope, new ManagementPath("Msvm_VirtualSystemExportSettingData"), null);
                using var settings = cls.CreateInstance();
                settings["CopySnapshotConfiguration"] = (byte)0; // all checkpoints
                settings["CopyVmRuntimeInformation"] = true;    // saved state
                settings["CopyVmStorage"] = true;
                settings["CreateVmExportSubdirectory"] = false;
                using var p = service.GetMethodParameters("ExportSystemDefinition");
                p["ComputerSystem"] = vm.Path.Path;
                p["ExportDirectory"] = directory;
                p["ExportSettingData"] = settings.GetText(TextFormat.CimDtd20);
                using var o = service.InvokeMethod("ExportSystemDefinition", p, null);
                CompleteOperation(scope, o, "export the VM", cancellationToken, terminateOnCancel: true);
                _logger.LogInformation("Exported VM {VmId} to {Directory}", vmId, directory);
            }, cancellationToken);

        // ── helpers ────────────────────────────────────────────────────────

        private ManagementScope Connect()
        {
            var scope = new ManagementScope($@"\\{_machine}\{Namespace}");
            try
            {
                scope.Connect();
            }
            catch (ManagementException ex)
            {
                throw new HyperVOperationException($"Cannot connect to Hyper-V WMI ({Namespace}). Is Hyper-V enabled?", null, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new HyperVOperationException("Access denied to Hyper-V. Run as administrator or join the 'Hyper-V Administrators' group.", null, ex);
            }
            return scope;
        }

        private static IEnumerable<ManagementBaseObject> Query(ManagementScope scope, string wql)
        {
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
            using var results = searcher.Get();
            foreach (var mo in results)
            {
                using (mo)
                    yield return mo;
            }
        }

        private static ManagementObject? QueryFirst(ManagementScope scope, string wql)
        {
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
                return mo;
            return null;
        }

        private static ManagementObject GetManagementService(ManagementScope scope)
            => QueryFirst(scope, "SELECT * FROM Msvm_VirtualSystemManagementService")
               ?? throw new HyperVOperationException("Msvm_VirtualSystemManagementService not found. Is the Hyper-V management service running?");

        private static ManagementObject GetComputerSystem(ManagementScope scope, Guid vmId)
            => QueryFirst(scope, $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'")
               ?? throw new HyperVOperationException($"VM {vmId} no longer exists.");

        private static ManagementObject GetRealizedSettings(ManagementScope scope, Guid vmId)
            => QueryFirst(scope, $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE InstanceID = 'Microsoft:{vmId:D}'")
               ?? throw new HyperVOperationException($"VM {vmId} no longer exists.");

        /// <summary>
        /// Handles the WMI method convention: 0 = done, 4096 = asynchronous
        /// job (wait for it), anything else = failure. With
        /// <paramref name="terminateOnCancel"/> a cancelled wait also asks
        /// Hyper-V to stop the job and waits until it has, so the caller can
        /// clean up files the job was writing; otherwise the job keeps running.
        /// </summary>
        private static void CompleteOperation(ManagementScope scope, ManagementBaseObject outParams, string what, CancellationToken ct,
            bool terminateOnCancel = false)
        {
            uint rc = ToUInt(outParams["ReturnValue"]);
            if (rc == ReturnOk)
                return;
            if (rc != ReturnJobStarted)
                throw new HyperVOperationException($"Failed to {what}: {DescribeReturnCode(rc)}", rc);

            string jobPath = outParams["Job"] as string ?? throw new HyperVOperationException($"Failed to {what}: job path missing");
            using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    if (terminateOnCancel)
                        TerminateJob(job);
                    ct.ThrowIfCancellationRequested();
                }
                job.Get();
                ushort state = ToUShort(job["JobState"]);
                if (state == 7) // Completed
                    return;
                if (state > 7) // Terminated / Killed / Exception
                {
                    string detail = job["ErrorDescription"] as string ?? $"job state {state}";
                    throw new HyperVOperationException($"Failed to {what}: {detail}");
                }
                Thread.Sleep(200);
            }
        }

        /// <summary>Requests Terminate (4) and waits up to a minute for the job to end.</summary>
        private static void TerminateJob(ManagementObject job)
        {
            try
            {
                using var p = job.GetMethodParameters("RequestStateChange");
                p["RequestedState"] = (ushort)4;
                using var o = job.InvokeMethod("RequestStateChange", p, null);
                var deadline = DateTime.UtcNow.AddMinutes(1);
                while (DateTime.UtcNow < deadline)
                {
                    job.Get();
                    if (ToUShort(job["JobState"]) >= 7)
                        return;
                    Thread.Sleep(200);
                }
            }
            catch (ManagementException)
            {
                // The job finished or vanished meanwhile; nothing left to stop.
            }
        }

        internal static string DescribeReturnCode(uint rc) => rc switch
        {
            32768 => "failed",
            32769 => "access denied",
            32770 => "not supported",
            32771 => "status unknown",
            32772 => "timeout",
            32773 => "invalid parameter",
            32774 => "system is in use",
            32775 => "invalid state for this operation",
            32776 => "incorrect data type",
            32777 => "system is not available",
            32778 => "out of memory",
            _ => $"error {rc}",
        };

        /// <summary>"Microsoft:GuestNetwork\{vm}\{nic}" → vm.</summary>
        internal static bool TryParseGuestNetworkVmId(string? instanceId, out Guid vmId)
        {
            vmId = default;
            const string prefix = @"Microsoft:GuestNetwork\";
            if (instanceId == null || !instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            string rest = instanceId.Substring(prefix.Length);
            int slash = rest.IndexOf('\\');
            return Guid.TryParse(slash < 0 ? rest : rest.Substring(0, slash), out vmId);
        }

        /// <summary>"Microsoft:{vm}\{device}..." → vm (settings of a realized VM).</summary>
        internal static bool TryParseSettingVmId(string? instanceId, out Guid vmId)
        {
            vmId = default;
            const string prefix = "Microsoft:";
            if (instanceId == null || !instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            string rest = instanceId.Substring(prefix.Length);
            int slash = rest.IndexOf('\\');
            return Guid.TryParse(slash < 0 ? rest : rest.Substring(0, slash), out vmId);
        }

        /// <summary>Type evidence from a switch's own port: the host computer = internal, a physical port = external.</summary>
        internal static SwitchKind? ClassifyHostSidePort(string[]? hostResource)
        {
            if (hostResource == null || hostResource.Length == 0)
                return null;
            return hostResource.Any(p => p.Contains("Msvm_ComputerSystem", StringComparison.OrdinalIgnoreCase))
                ? SwitchKind.Internal
                : SwitchKind.External; // Msvm_ExternalEthernetPort, Msvm_WiFiPort
        }

        /// <summary>IPv4 "a.b.c.d" inside "net/len".</summary>
        internal static bool IsInPrefix(string ip, string cidr)
        {
            int slash = cidr.IndexOf('/');
            if (slash < 0
                || !System.Net.IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || !System.Net.IPAddress.TryParse(cidr[..slash], out var net) || net.AddressFamily != addr.AddressFamily
                || !int.TryParse(cidr[(slash + 1)..], out int len) || len is < 0 or > 32)
                return false;
            uint a = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(addr.GetAddressBytes());
            uint n = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(net.GetAddressBytes());
            uint mask = len == 0 ? 0 : uint.MaxValue << (32 - len);
            return (a & mask) == (n & mask);
        }

        internal static Guid? ParseHostResourceSwitch(string[]? hostResource)
        {
            if (hostResource == null)
                return null;
            foreach (string path in hostResource)
            {
                if (!path.Contains("Msvm_VirtualEthernetSwitch", StringComparison.OrdinalIgnoreCase))
                    continue;
                var m = HostResourceName.Match(path);
                if (m.Success && Guid.TryParse(m.Groups["id"].Value, out var id))
                    return id;
            }
            return null;
        }

        internal static int ParseGeneration(string? subType)
            => subType != null && subType.EndsWith(":2", StringComparison.Ordinal) ? 2 : 1;

        private static string JoinNotes(object? notes) => notes switch
        {
            string[] lines => string.Join("\r\n", lines),
            string s => s,
            _ => "",
        };

        private static DateTime? ToDateTime(object? dmtf)
        {
            if (dmtf is not string s || s.Length == 0)
                return null;
            try { return ManagementDateTimeConverter.ToDateTime(s); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static ushort ToUShort(object? v) => v == null ? (ushort)0 : Convert.ToUInt16(v);
        private static uint ToUInt(object? v) => v == null ? 0u : Convert.ToUInt32(v);
        private static ulong ToULong(object? v) => v == null ? 0ul : Convert.ToUInt64(v);
    }
}
