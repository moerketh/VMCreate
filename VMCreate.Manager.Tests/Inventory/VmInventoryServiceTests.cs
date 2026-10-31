using VMCreate.Manager.HyperV;
using VMCreate.Manager.Inventory;

namespace VMCreate.Manager.Tests.Inventory
{
    [TestClass]
    public class VmInventoryServiceTests
    {
        private static readonly Guid VmA = Guid.Parse("0239E0BC-1B40-45E8-A815-B4472B9CEBFE");
        private static readonly Guid VmB = Guid.Parse("0FE004EC-F031-415B-8AEE-F761F61CA6CE");
        private static readonly Guid SwitchId = Guid.Parse("C08CB7B8-9B3C-408E-8E30-5E16A3AEB444");

        private static HostSnapshot Snapshot(
            IReadOnlyList<RawVm>? vms = null,
            IReadOnlyList<RawVmSummary>? summaries = null,
            IReadOnlyList<RawGuestNetworkAdapter>? adapters = null,
            IReadOnlyList<RawSwitchPort>? ports = null,
            IReadOnlyList<RawMemorySetting>? memory = null,
            IReadOnlyList<RawCheckpoint>? checkpoints = null)
            => new(
                vms ?? Array.Empty<RawVm>(),
                summaries ?? Array.Empty<RawVmSummary>(),
                adapters ?? Array.Empty<RawGuestNetworkAdapter>(),
                ports ?? Array.Empty<RawSwitchPort>(),
                new[] { new RawSwitch(SwitchId, "Default Switch") },
                memory ?? Array.Empty<RawMemorySetting>(),
                checkpoints ?? Array.Empty<RawCheckpoint>());

        private static RawVm Vm(Guid id, string name, ushort state = 3, ulong onTime = 0)
            => new(id, name, state, onTime, 2, @"D:\Hyper-V\" + name, "", null);

        [TestMethod]
        public void Build_JoinsAllSourcesPerVm()
        {
            var result = VmInventoryService.Build(Snapshot(
                vms: new[] { Vm(VmA, "Parrot", state: 2, onTime: 90_000) },
                summaries: new[] { new RawVmSummary(VmA, 4, 12, 4096, 2, "Linux") },
                adapters: new[] { new RawGuestNetworkAdapter(VmA, new[] { "fe80::1", "172.20.1.5", "2001:db8::5" }) },
                ports: new[] { new RawSwitchPort(VmA, SwitchId), new RawSwitchPort(VmA, null) },
                memory: new[] { new RawMemorySetting(VmA, 8192, false) },
                checkpoints: new[] { new RawCheckpoint(VmA, "baseline"), new RawCheckpoint(VmB, "other") }));

            var vm = result.Single();
            Assert.AreEqual(VmState.Running, vm.State);
            Assert.AreEqual(TimeSpan.FromSeconds(90), vm.Uptime);
            Assert.AreEqual(4, vm.ProcessorCount);
            Assert.AreEqual(12, vm.CpuPercent);
            Assert.AreEqual(4096UL, vm.AssignedMemoryMb);
            Assert.AreEqual(8192UL, vm.StartupMemoryMb);
            CollectionAssert.AreEqual(new[] { "172.20.1.5", "2001:db8::5" }, vm.IpAddresses.ToArray());
            CollectionAssert.AreEqual(new[] { "Default Switch", "Not connected" }, vm.Switches.ToArray());
            Assert.AreEqual(1, vm.CheckpointCount);
        }

        [TestMethod]
        public void Build_OffVm_HasNoLiveCpuOrMemory()
        {
            var result = VmInventoryService.Build(Snapshot(
                vms: new[] { Vm(VmA, "Off VM") },
                summaries: new[] { new RawVmSummary(VmA, 2, 0, 0, null, null) }));

            Assert.IsNull(result[0].CpuPercent);
            Assert.IsNull(result[0].AssignedMemoryMb);
        }

        [TestMethod]
        public void Build_SortsByNameCaseInsensitive()
        {
            var result = VmInventoryService.Build(Snapshot(vms: new[] { Vm(VmA, "zeta"), Vm(VmB, "Alpha") }));
            CollectionAssert.AreEqual(new[] { "Alpha", "zeta" }, result.Select(v => v.Name).ToArray());
        }

        [TestMethod]
        public void Build_SwitchPortToUnknownSwitch_ShowsNotConnected()
        {
            var result = VmInventoryService.Build(Snapshot(
                vms: new[] { Vm(VmA, "x") },
                ports: new[] { new RawSwitchPort(VmA, Guid.NewGuid()) }));
            CollectionAssert.AreEqual(new[] { "Not connected" }, result[0].Switches.ToArray());
        }

        [TestMethod]
        [DataRow((ushort)2, VmState.Running)]
        [DataRow((ushort)3, VmState.Off)]
        [DataRow((ushort)6, VmState.Saved)]
        [DataRow((ushort)32769, VmState.Saved)]
        [DataRow((ushort)9, VmState.Paused)]
        [DataRow((ushort)32768, VmState.Paused)]
        [DataRow((ushort)10, VmState.Starting)]
        [DataRow((ushort)4, VmState.Stopping)]
        [DataRow((ushort)32773, VmState.Saving)]
        [DataRow((ushort)1, VmState.Unknown)]
        public void FromEnabledState_Maps(ushort enabledState, VmState expected)
        {
            Assert.AreEqual(expected, VmStateMapper.FromEnabledState(enabledState));
        }

        [TestMethod]
        public void ParseHelpers_HandleRealWmiStrings()
        {
            // Strings captured from root\virtualization\v2 on a Windows 11 host.
            Assert.IsTrue(WmiHyperVHost.TryParseGuestNetworkVmId(
                @"Microsoft:GuestNetwork\0239E0BC-1B40-45E8-A815-B4472B9CEBFE\1344C4B5-9C3D-41F3-A3C9-1B631B826664", out var vm));
            Assert.AreEqual(VmA, vm);

            Assert.IsTrue(WmiHyperVHost.TryParseSettingVmId(
                @"Microsoft:0239E0BC-1B40-45E8-A815-B4472B9CEBFE\4764334d-e001-4176-82ee-5594ec9b530e", out vm));
            Assert.AreEqual(VmA, vm);

            Assert.IsFalse(WmiHyperVHost.TryParseSettingVmId(@"Microsoft:Definition\B637F347-6A0E-4DEC-AF52-BD70CB80A21D\Default", out _));

            Assert.AreEqual(SwitchId, WmiHyperVHost.ParseHostResourceSwitch(new[]
            {
                @"\\HOST\root\virtualization\v2:Msvm_VirtualEthernetSwitch.CreationClassName=""Msvm_VirtualEthernetSwitch"",Name=""C08CB7B8-9B3C-408E-8E30-5E16A3AEB444""",
            }));
            Assert.IsNull(WmiHyperVHost.ParseHostResourceSwitch(new[]
            {
                @"\\HOST\root\virtualization\v2:Msvm_ComputerSystem.CreationClassName=""Msvm_ComputerSystem"",Name=""HOST""",
            }));

            Assert.AreEqual(2, WmiHyperVHost.ParseGeneration("Microsoft:Hyper-V:SubType:2"));
            Assert.AreEqual(1, WmiHyperVHost.ParseGeneration("Microsoft:Hyper-V:SubType:1"));
        }
    }
}
