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
            "Get-VMDvdDrive" => new PowerShellResult { Output = DvdDevices(@"D:\media\installer.iso", @"D:\media\cidata.iso") },
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

        private static Collection<PSObject> DvdDevices(params string[] paths)
        {
            var collection = new Collection<PSObject>();
            for (var i = 0; i < paths.Length; i++)
            {
                var pso = new PSObject(new TestDevice(paths[i]));
                pso.Properties.Add(new PSNoteProperty("Path", paths[i]));
                pso.Properties.Add(new PSNoteProperty("ControllerNumber", 0));
                pso.Properties.Add(new PSNoteProperty("ControllerLocation", i));
                collection.Add(pso);
            }
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
            CollectionAssert.AreEqual(new[] { "hdd0", @"D:\media\installer.iso", @"D:\media\cidata.iso", "nic0" }, actualOrder);
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

        [TestMethod]
        public async Task AttachIsoToNewDvdDrive_AlwaysAddsDrive_NeverRetargetsExisting()
        {
            // Auxiliary media (cidata/unattend seeds) must not reuse the installer's
            // drive — a bare Set-VMDvdDrive acts on the first drive and would swap
            // the (bootable) installer ISO for the non-bootable seed.
            await _bootManager.AttachIsoToNewDvdDrive(Plan(), @"C:\iso\seed.iso", CancellationToken.None);

            var add = _captured["Add-VMDvdDrive"];
            Assert.AreEqual(@"C:\iso\seed.iso", add.Single(p => p.Key == "Path").Value);
            Assert.IsFalse(_captured.ContainsKey("Set-VMDvdDrive"), "must not retarget an existing drive");
        }

        [TestMethod]
        public async Task RemoveBootDvd_DetachesOnlyTheDriveHoldingTheMedia()
        {
            await _bootManager.RemoveBootDvd(Plan(), @"D:\media\installer.iso", CancellationToken.None);

            var set = _captured["Set-VMDvdDrive"];
            Assert.AreEqual(0, set.Single(p => p.Key == "ControllerNumber").Value);
            Assert.AreEqual(0, set.Single(p => p.Key == "ControllerLocation").Value, "installer.iso is on the first emulated drive");
            Assert.IsNull(set.Single(p => p.Key == "Path").Value);
        }

        [TestMethod]
        public async Task RemoveBootDvd_MatchesSecondDrive_ByPath()
        {
            await _bootManager.RemoveBootDvd(Plan(), @"D:\media\cidata.iso", CancellationToken.None);

            var set = _captured["Set-VMDvdDrive"];
            Assert.AreEqual(1, set.Single(p => p.Key == "ControllerLocation").Value);
        }

        [TestMethod]
        public async Task RemoveBootDvd_WhenMediaNotAttached_DoesNotTouchAnyDrive()
        {
            await _bootManager.RemoveBootDvd(Plan(), @"D:\media\nope.iso", CancellationToken.None);

            Assert.IsFalse(_captured.ContainsKey("Set-VMDvdDrive"));
        }

        [TestMethod]
        public async Task SetSecureBoot_WhenPlanRequestsSecureBoot_EmitsOnWithTemplate()
        {
            // Regression pin for the metadata-to-firmware gap: gallery items
            // declare SecureBoot "true" (e.g. FLARE VM), and the plan carries
            // the parsed boolean — the sink must emit EnableSecureBoot=On
            // with the declared template, never a silent Off.
            var plan = VmDeploymentPlan.FromSettings(new VmSettings
            {
                VMName = "SecureBootTestVM",
                SecureBoot = true,
                SecureBootTemplate = "MicrosoftWindows"
            });

            await _bootManager.SetSecureBoot(plan, "MicrosoftWindows", CancellationToken.None);

            var firmware = _captured["Set-VMFirmware"];
            Assert.AreEqual("On", firmware.Single(p => p.Key == "EnableSecureBoot").Value,
                "a plan built with SecureBoot=true must reach the firmware sink as On");
            Assert.AreEqual("MicrosoftWindows", firmware.Single(p => p.Key == "SecureBootTemplate").Value);
        }

        [TestMethod]
        public async Task SetSecureBoot_WhenPlanDoesNotRequestSecureBoot_EmitsOff()
        {
            var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "PlainVm" });

            await _bootManager.SetSecureBoot(plan, null, CancellationToken.None);

            var firmware = _captured["Set-VMFirmware"];
            Assert.AreEqual("Off", firmware.Single(p => p.Key == "EnableSecureBoot").Value);
        }
    }
}
