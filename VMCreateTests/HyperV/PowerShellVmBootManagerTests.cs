using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV;

namespace VMCreate.Tests.HyperV
{
    [TestClass]
    public sealed class PowerShellVmBootManagerTests
    {
        private Mock<IPowerShellExecutor> _executor = null!;
        private PowerShellVmBootManager _bootManager = null!;
        private Dictionary<string, List<KeyValuePair<string, object?>>> _captured = null!;
        private bool _hasHardDrive = true;
        private bool _failSetFirmware;

        private sealed class TestDevice
        {
            public string Id { get; }
            public TestDevice(string id) => Id = id;
        }

        [TestInitialize]
        public void Setup()
        {
            _executor = new Mock<IPowerShellExecutor>();
            _bootManager = new PowerShellVmBootManager(_executor.Object, Mock.Of<ILogger<PowerShellVmBootManager>>());
            _captured = new Dictionary<string, List<KeyValuePair<string, object?>>>();
            _executor
                .Setup(e => e.RunCommandAsync(
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<KeyValuePair<string, object?>>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, IEnumerable<KeyValuePair<string, object?>>, CancellationToken>((command, parameters, _) =>
                    _captured[command] = (parameters ?? Array.Empty<KeyValuePair<string, object?>>()).ToList())
                .Returns<string, IEnumerable<KeyValuePair<string, object?>>, CancellationToken>((command, _, _) =>
                    Task.FromResult(ResultFor(command)));
        }

        private PowerShellResult ResultFor(string command) => command switch
        {
            "Set-VMFirmware" when _failSetFirmware => new PowerShellResult { HadErrors = true, ErrorSummary = "firmware exploded" },
            "Get-VMHardDiskDrive" => new PowerShellResult { Output = _hasHardDrive ? Devices("hdd0") : new Collection<PSObject>() },
            "Get-VMDvdDrive" => new PowerShellResult { Output = Devices("installer-dvd", "cidata-dvd") },
            "Get-VMNetworkAdapter" => new PowerShellResult { Output = Devices("nic0") },
            _ => new PowerShellResult(),
        };

        private static Collection<PSObject> Devices(params string[] ids)
        {
            var collection = new Collection<PSObject>();
            foreach (var id in ids)
                collection.Add(new PSObject(new TestDevice(id)));
            return collection;
        }

        private static VmDeploymentPlan Plan() => VmDeploymentPlan.FromSettings(new VmSettings { VMName = "BootOrderTestVM" });

        [TestMethod]
        public async Task SetFirstBootToHardDrive_SetsExplicitBootOrder_HardDriveThenDvdsThenNetwork()
        {
            // Regression pin for the PXE stall: fresh VMs enumerate the NIC first in
            // firmware, and -FirstBootDevice kept that order for the remaining slots
            // (HDD, NIC, DVD) so a blank disk fell through to PXE instead of the
            // installer DVD. The full -BootOrder must put every DVD above the NIC.
            await _bootManager.SetFirstBootToHardDrive(Plan(), CancellationToken.None);

            var firmware = _captured["Set-VMFirmware"];
            Assert.IsFalse(firmware.Any(p => p.Key == "FirstBootDevice"), "Must not use the partial-reorder FirstBootDevice parameter.");
            var value = firmware.Single(p => p.Key == "BootOrder").Value;
            Assert.IsInstanceOfType(value, typeof(object[]));
            var actualOrder = ((object[])value!).Cast<PSObject>().Select(o => ((TestDevice)o.BaseObject).Id).ToArray();
            CollectionAssert.AreEqual(new[] { "hdd0", "installer-dvd", "cidata-dvd", "nic0" }, actualOrder);
        }

        [TestMethod]
        public async Task SetFirstBootToHardDrive_WithoutHardDrive_ThrowsAndDoesNotTouchFirmware()
        {
            _hasHardDrive = false;

            var ex = await Assert.ThrowsAsync<Exception>(
                () => _bootManager.SetFirstBootToHardDrive(Plan(), CancellationToken.None));
            StringAssert.Contains(ex.Message, "No hard disk drive");
            Assert.IsFalse(_captured.ContainsKey("Set-VMFirmware"));
        }

        [TestMethod]
        public async Task SetFirstBootToHardDrive_WhenFirmwareUpdateFails_Throws()
        {
            _failSetFirmware = true;

            var ex = await Assert.ThrowsAsync<Exception>(
                () => _bootManager.SetFirstBootToHardDrive(Plan(), CancellationToken.None));
            StringAssert.Contains(ex.Message, "Failed to set boot order");
            StringAssert.Contains(ex.Message, "firmware exploded");
        }
    }
}
