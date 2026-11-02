using System.Net;
using System.Net.Sockets;
using VMCreate.Manager.HyperV;

namespace VMCreate.Manager.Inventory
{
    public interface IVmInventoryService
    {
        Task<IReadOnlyList<VmSummary>> GetAllAsync(CancellationToken cancellationToken = default);

        /// <summary>Virtual switches as of the last <see cref="GetAllAsync"/>.</summary>
        IReadOnlyList<RawSwitch> Switches { get; }

        /// <summary>See <see cref="IHyperVHost.WatchChanges"/>.</summary>
        IDisposable WatchChanges(Action onChange);
    }

    public sealed class VmInventoryService : IVmInventoryService
    {
        internal const string NotConnected = "Not connected";

        private readonly IHyperVHost _host;

        public VmInventoryService(IHyperVHost host) => _host = host;

        public IReadOnlyList<RawSwitch> Switches { get; private set; } = Array.Empty<RawSwitch>();

        public async Task<IReadOnlyList<VmSummary>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = await _host.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            Switches = snapshot.Switches.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return Build(snapshot);
        }

        public IDisposable WatchChanges(Action onChange) => _host.WatchChanges(onChange);

        /// <summary>Joins the raw WMI records into one summary per VM, sorted by name.</summary>
        internal static IReadOnlyList<VmSummary> Build(HostSnapshot snapshot)
        {
            var summaries = snapshot.Summaries.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            var memory = snapshot.Memory.GroupBy(m => m.VmId).ToDictionary(g => g.Key, g => g.First());
            var switchesById = snapshot.Switches.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            var ips = snapshot.GuestAdapters.ToLookup(a => a.VmId);
            var ports = snapshot.SwitchPorts.ToLookup(p => p.VmId);
            var checkpoints = snapshot.Checkpoints.ToLookup(c => c.VmId);

            return snapshot.Vms
                .Select(vm =>
                {
                    summaries.TryGetValue(vm.Id, out var s);
                    memory.TryGetValue(vm.Id, out var mem);
                    var state = VmStateMapper.FromEnabledState(vm.EnabledState);
                    bool running = state != VmState.Off && state != VmState.Saved;
                    return new VmSummary
                    {
                        Id = vm.Id,
                        Name = vm.Name,
                        State = state,
                        Uptime = TimeSpan.FromMilliseconds(vm.OnTimeMilliseconds),
                        Generation = vm.Generation,
                        ConfigurationPath = vm.ConfigurationPath,
                        Notes = vm.Notes,
                        CreatedAt = vm.CreatedAt,
                        ProcessorCount = s?.ProcessorCount ?? 0,
                        CpuPercent = running ? s?.ProcessorLoadPercent : null,
                        AssignedMemoryMb = running && s?.MemoryUsageMb is > 0 ? s.MemoryUsageMb : null,
                        StartupMemoryMb = mem?.StartupMb ?? 0,
                        DynamicMemory = mem?.DynamicMemoryEnabled ?? false,
                        GuestOperatingSystem = s?.GuestOperatingSystem,
                        IpAddresses = OrderIpAddresses(ips[vm.Id].SelectMany(a => a.IpAddresses)),
                        Switches = ports[vm.Id]
                            .Select(p => p.Connected && p.SwitchId is Guid id && switchesById.TryGetValue(id, out var sw) ? sw.Name : NotConnected)
                            .ToList(),
                        Exposure = ports[vm.Id]
                            .Where(p => p.Connected && p.SwitchId != null)
                            .Select(p => switchesById.TryGetValue(p.SwitchId!.Value, out var sw) ? ExposureOf(sw) : NetworkExposure.None)
                            .DefaultIfEmpty(NetworkExposure.None)
                            .Max(),
                        DisconnectedAdapterCount = ports[vm.Id].Count(p => !p.Connected && p.SwitchId != null),
                        CheckpointCount = checkpoints[vm.Id].Count(),
                    };
                })
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static NetworkExposure ExposureOf(RawSwitch sw) => sw.Kind switch
        {
            SwitchKind.External => NetworkExposure.External,
            SwitchKind.Internal => sw.Nat ? NetworkExposure.Nat : NetworkExposure.Internal,
            _ => NetworkExposure.Private,
        };

        /// <summary>IPv4 first, then global IPv6; link-local IPv6 is dropped (useless for connecting).</summary>
        internal static IReadOnlyList<string> OrderIpAddresses(IEnumerable<string> addresses)
        {
            return addresses
                .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
                .Where(ip => ip != null && !ip.IsIPv6LinkLocal)
                .Distinct()
                .OrderBy(ip => ip!.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Select(ip => ip!.ToString())
                .ToList();
        }
    }
}
