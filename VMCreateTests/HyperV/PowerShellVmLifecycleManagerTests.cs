using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV;

namespace VMCreate.Tests.HyperV
{
    /// <summary>
    /// <see cref="PowerShellVmLifecycleManager.StartVM"/> must surface Hyper-V's
    /// reason when a VM does not start. Start-VM reports e.g. "not enough
    /// memory" (0x8007000E) on the error stream instead of throwing; it used to
    /// be discarded, so a deploy waited minutes and then reported only a
    /// generic "did not start within the timeout".
    /// </summary>
    [TestClass]
    public sealed class PowerShellVmLifecycleManagerTests
    {
        private const string OutOfMemory =
            "'t-vm' failed to start. Not enough memory in the system to start the virtual machine t-vm. (0x8007000E)";

        private Mock<IPowerShellExecutor> _executor = null!;
        private PowerShellVmLifecycleManager _manager = null!;
        private readonly VmDeploymentPlan _plan = new("t-vm");

        [TestInitialize]
        public void Setup()
        {
            _executor = new Mock<IPowerShellExecutor>();
            _manager = new PowerShellVmLifecycleManager(_executor.Object, Mock.Of<ILogger<PowerShellVmLifecycleManager>>());
        }

        private void StartVmReturns(PowerShellResult result) =>
            _executor.Setup(e => e.RunCommandAsync("Start-VM", It.IsAny<IEnumerable<KeyValuePair<string, object?>>>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(result);

        private void VmStateIs(string state) =>
            _executor.Setup(e => e.RunScriptAsync(It.Is<string>(s => s.Contains("Get-VM") && s.Contains("'t-vm'")), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new PowerShellResult { Output = new Collection<PSObject> { new PSObject(state) } });

        [TestMethod]
        public async Task StartVM_Clean_DoesNotQueryState()
        {
            StartVmReturns(new PowerShellResult());

            await _manager.StartVM(_plan, CancellationToken.None);

            _executor.Verify(e => e.RunScriptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task StartVM_ErrorAndVmOff_ThrowsWithHyperVReason()
        {
            StartVmReturns(new PowerShellResult { HadErrors = true, ErrorSummary = OutOfMemory });
            VmStateIs("Off");

            var ex = await Assert.ThrowsExactlyAsync<Exception>(() => _manager.StartVM(_plan, CancellationToken.None));

            StringAssert.Contains(ex.Message, "Not enough memory", "the Hyper-V reason must reach the user");
            StringAssert.Contains(ex.Message, "0x8007000E");
            StringAssert.Contains(ex.Message, "t-vm");
            StringAssert.Contains(ex.Message, "Off");
        }

        [TestMethod]
        public async Task StartVM_ErrorButVmRunning_Continues()
        {
            StartVmReturns(new PowerShellResult { HadErrors = true, ErrorSummary = "transient" });
            VmStateIs("Running");

            await _manager.StartVM(_plan, CancellationToken.None);
        }
    }
}
