using VMCreate.Manager.HyperV;
using VMCreate.Manager.Inventory;

namespace VMCreate.Manager.Tests.Inventory
{
    [TestClass]
    public class NetworkExposureTests
    {
        private static readonly Guid Vm = Guid.NewGuid();
        private static readonly RawSwitch External = new(Guid.NewGuid(), "LAN", SwitchKind.External);
        private static readonly RawSwitch Nat = new(Guid.NewGuid(), "Default Switch", SwitchKind.Internal, Nat: true);
        private static readonly RawSwitch Internal = new(Guid.NewGuid(), "HostOnly", SwitchKind.Internal);
        private static readonly RawSwitch Private = new(Guid.NewGuid(), "Lab", SwitchKind.Private);

        private static RawSwitchPort P(Guid? switchId, bool connected = true) => new(Vm, switchId, connected);

        private static VmSummary Build(params RawSwitchPort[] ports)
            => VmInventoryService.Build(new HostSnapshot(
                new[] { new RawVm(Vm, "vm", 2, 0, 2, null, "", null) },
                Array.Empty<RawVmSummary>(),
                Array.Empty<RawGuestNetworkAdapter>(),
                ports,
                new[] { External, Nat, Internal, Private },
                Array.Empty<RawMemorySetting>(),
                Array.Empty<RawCheckpoint>())).Single();

        [TestMethod]
        public void Exposure_IsTheWidestConnectedAdapter()
        {
            Assert.AreEqual(NetworkExposure.External, Build(P(Private.Id), P(External.Id)).Exposure);
            Assert.AreEqual(NetworkExposure.Nat, Build(P(Internal.Id), P(Nat.Id)).Exposure);
            Assert.AreEqual(NetworkExposure.Internal, Build(P(Internal.Id)).Exposure);
            Assert.AreEqual(NetworkExposure.Private, Build(P(Private.Id)).Exposure);
            Assert.AreEqual(NetworkExposure.None, Build().Exposure);
            Assert.AreEqual(NetworkExposure.None, Build(P(null, connected: false)).Exposure);
        }

        [TestMethod]
        public void DisconnectedAdapter_CountsAsIsolatedAndShowsNotConnected()
        {
            var vm = Build(P(External.Id, connected: false), P(Private.Id), P(null, connected: false));

            Assert.AreEqual(NetworkExposure.Private, vm.Exposure, "a disconnected external adapter does not expose the VM");
            Assert.AreEqual(1, vm.DisconnectedAdapterCount, "only adapters that remember a switch can be reconnected");
            CollectionAssert.AreEqual(new[] { "Not connected", "Lab", "Not connected" }, vm.Switches.ToArray());
        }

        [TestMethod]
        public void ClassifyHostSidePort_HostIsInternal_PhysicalIsExternal()
        {
            Assert.AreEqual(SwitchKind.Internal, WmiHyperVHost.ClassifyHostSidePort(new[]
                { @"\\HOST\root\virtualization\v2:Msvm_ComputerSystem.CreationClassName=""Msvm_ComputerSystem"",Name=""HOST""" }));
            Assert.AreEqual(SwitchKind.External, WmiHyperVHost.ClassifyHostSidePort(new[]
                { @"\\HOST\root\virtualization\v2:Msvm_ExternalEthernetPort.CreationClassName=""Msvm_ExternalEthernetPort"",DeviceID=""{X}""" }));
            Assert.IsNull(WmiHyperVHost.ClassifyHostSidePort(null));
            Assert.IsNull(WmiHyperVHost.ClassifyHostSidePort(Array.Empty<string>()));
        }

        [TestMethod]
        [DataRow("192.168.100.1", "192.168.100.0/24", true)]
        [DataRow("192.168.101.1", "192.168.100.0/24", false)]
        [DataRow("172.26.224.1", "172.26.224.0/20", true)]
        [DataRow("172.26.240.1", "172.26.224.0/20", false)]
        [DataRow("10.0.0.1", "0.0.0.0/0", true)]
        [DataRow("fe80::1", "192.168.100.0/24", false)]
        [DataRow("192.168.100.1", "garbage", false)]
        public void IsInPrefix(string ip, string cidr, bool expected)
            => Assert.AreEqual(expected, WmiHyperVHost.IsInPrefix(ip, cidr));
    }
}
