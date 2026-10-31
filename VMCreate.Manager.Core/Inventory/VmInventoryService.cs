using System.Net;
using System.Net.Sockets;
using VMCreate.Manager.HyperV;

namespace VMCreate.Manager.Inventory
{
    public interface IVmInventoryService
    {
        Task<IReadOnlyList<VmSummary>> GetAllAsync(CancellationToken cancellationToken = default);

        /// <summary>See <see cref="IHyperVHost.WatchChanges"/>.</summary>
        IDisposable WatchChanges(Action onChange);
    }

    public sealed class VmInventoryService : IVmInventoryService
    {
        internal const string NotConnected = "Not connected";

        private readonly IHyperVHost _host;

        public VmInventoryService(IHyperVHost host) => _host = host;

        public async Task<IReadOnlyList<VmSummary>> GetAllAsync(CancellationToken cancellationToken = default)
            => Build(await _host.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false));

        public IDisposable WatchChanges(Action onChange) => _host.WatchChanges(onChange);

        /// <summary>Joins the raw WMI records into one summary per VM, sorted by name.</summary>
        internal static IReadOnlyList<VmSummary> Build(HostSnapshot snapshot)
        {
            var summaries = snapshot.Summaries.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            var memory = snapshot.Memory.GroupBy(m => m.VmId).ToDictionary(g => g.Key, g => g.First());
            var switchNames = snapshot.Switches.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);
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
                            .Select(p => p.SwitchId is Guid id && switchNames.TryGetValue(id, out var n) ? n : NotConnected)
                            .ToList(),
                        CheckpointCount = checkpoints[vm.Id].Count(),
                    };
                })
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

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
