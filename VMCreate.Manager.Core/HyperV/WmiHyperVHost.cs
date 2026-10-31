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

            var ports = new List<RawSwitchPort>();
            foreach (var mo in Query(scope, "SELECT InstanceID, HostResource FROM Msvm_EthernetPortAllocationSettingData"))
            {
                if (!TryParseSettingVmId(mo["InstanceID"] as string, out var vmId) || !computerSystems.ContainsKey(vmId))
                    continue;
                ports.Add(new RawSwitchPort(vmId, ParseHostResourceSwitch(mo["HostResource"] as string[])));
            }

            var switches = new List<RawSwitch>();
            foreach (var mo in Query(scope, "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch"))
            {
                if (Guid.TryParse(mo["Name"] as string, out var id))
                    switches.Add(new RawSwitch(id, mo["ElementName"] as string ?? ""));
            }

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
        /// job (wait for it), anything else = failure.
        /// </summary>
        private static void CompleteOperation(ManagementScope scope, ManagementBaseObject outParams, string what, CancellationToken ct)
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
                ct.ThrowIfCancellationRequested();
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
