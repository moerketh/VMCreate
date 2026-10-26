using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV;

namespace VMCreate.Tests.HyperV
{
    /// <summary>
    /// Pins the switch-selection contract of
    /// <see cref="PowerShellVmNetworkManager"/>: the VM's NIC connects to
    /// <c>plan.SwitchName</c> when set (CLI --switch) and falls back to the
    /// Default Switch otherwise, while the temporary post-boot SSH NIC stays
    /// pinned to the Default Switch (it is deploy plumbing, not part of the
    /// guest's final network).
    /// </summary>
    [TestClass]
    public sealed class PowerShellVmNetworkManagerTests
    {
        private Mock<IPowerShellExecutor> _executor = null!;
        private PowerShellVmNetworkManager _manager = null!;

        [TestInitialize]
        public void Setup()
        {
            _executor = new Mock<IPowerShellExecutor>();
            _executor
                .Setup(e => e.RunCommandAsync(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, object?>>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellResult());
            _manager = new PowerShellVmNetworkManager(_executor.Object, Mock.Of<ILogger<PowerShellVmNetworkManager>>());
        }

        private static VmDeploymentPlan Plan(string? switchName = null) =>
            new VmDeploymentPlan("TestVM", switchName: switchName);

        [TestMethod]
        public async Task ConnectNetworkAdapter_UsesPlanSwitchName_WhenSet()
        {
            await _manager.ConnectNetworkAdapter(Plan("Lab External"), CancellationToken.None);

            _executor.Verify(e => e.RunCommandAsync("Connect-VMNetworkAdapter",
                It.Is<IEnumerable<KeyValuePair<string, object?>>>(p => Value(p, "SwitchName") as string == "Lab External"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ConnectNetworkAdapter_FallsBackToDefaultSwitch_WhenPlanHasNone()
        {
            await _manager.ConnectNetworkAdapter(Plan(), CancellationToken.None);

            _executor.Verify(e => e.RunCommandAsync("Connect-VMNetworkAdapter",
                It.Is<IEnumerable<KeyValuePair<string, object?>>>(p => (Value(p, "SwitchName") as string) == "Default Switch"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ConnectNetworkAdapter_FailsLoudly_OnUnknownSwitch()
        {
            // No pre-validation: Connect-VMNetworkAdapter's own error is the
            // loud failure surface (same fail-loudly contract as the rest
            // of the CLI). The manager must propagate, not swallow.
            _executor
                .Setup(e => e.RunCommandAsync(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, object?>>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellResult { HadErrors = true, ErrorSummary = "Switch 'Nope' not found" });

            var ex = await Assert.ThrowsAsync<Exception>(() =>
                _manager.ConnectNetworkAdapter(Plan("Nope"), CancellationToken.None));
            StringAssert.Contains(ex.Message, "Switch 'Nope' not found");
        }

        [TestMethod]
        public async Task TemporaryNic_IsAlwaysPinnedToDefaultSwitch_EvenWithPlanSwitchSet()
        {
            await _manager.AddTemporaryNetworkAdapter(Plan("Lab External"), CancellationToken.None);

            _executor.Verify(e => e.RunCommandAsync("Add-VMNetworkAdapter",
                It.Is<IEnumerable<KeyValuePair<string, object?>>>(p =>
                    (Value(p, "SwitchName") as string) == "Default Switch"
                    && (Value(p, "Name") as string) == "VMCreate Temp"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task SwitchExists_TrueWhenGetVmswitchReturnsARow()
        {
            _executor
                .Setup(e => e.RunCommandAsync("Get-VMSwitch", It.IsAny<IEnumerable<KeyValuePair<string, object?>>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellResult { Output = new() { new System.Management.Automation.PSObject() } });

            Assert.IsTrue(await _manager.SwitchExistsAsync("Lab External", CancellationToken.None));
        }

        [TestMethod]
        public async Task SwitchExists_FalseWhenGetVmswitchReturnsNothing()
        {
            // A name that matches no switch yields an empty result
            // (SilentlyContinue), not an error — absence is the answer.
            _executor
                .Setup(e => e.RunCommandAsync("Get-VMSwitch", It.IsAny<IEnumerable<KeyValuePair<string, object?>>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellResult());

            Assert.IsFalse(await _manager.SwitchExistsAsync("Nope", CancellationToken.None));
        }

        private static object? Value(IEnumerable<KeyValuePair<string, object?>> parameters, string key)
        {
            foreach (var pair in parameters)
            {
                if (pair.Key == key)
                    return pair.Value;
            }
            return null;
        }
    }
}