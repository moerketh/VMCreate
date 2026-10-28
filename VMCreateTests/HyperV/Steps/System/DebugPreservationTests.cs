using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Pins the debug-preservation switch. Debug builds are always
    /// enabled (a Debug CLI deploy keeps the VM reachable); Release
    /// builds enable it via an attached debugger or
    /// <c>VMCREATE_DEBUG_PRESERVE=1</c>. Tests pin both sides through the
    /// internal override; the build-default test compiles only under
    /// DEBUG (the Release CI run would otherwise assert the Release
    /// default against a Debug expectation).
    /// Not parallelized: the switch is process-wide state.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class DebugPreservationTests
    {
        [TestInitialize]
        public void Reset()
        {
            DebugPreservation.TestOverride = null;
            Environment.SetEnvironmentVariable(DebugPreservation.EnvironmentVariable, null);
        }

        [TestCleanup]
        public void Cleanup()
        {
            DebugPreservation.TestOverride = null;
            Environment.SetEnvironmentVariable(DebugPreservation.EnvironmentVariable, null);
        }

#if DEBUG
        [TestMethod]
        public void IsEnabled_DebugBuildsAlwaysEnabled()
        {
            // Valid only when the test assembly compiles as Debug: the
            // default state must be ENABLED — every debug-build deployment
            // preserves the VM. (A Release run pins the same behavior via
            // TestOverride in the tests below.)
            Assert.IsTrue(DebugPreservation.IsEnabled);
        }
#endif

        [TestMethod]
        public void IsEnabled_ReleaseBuild_EnableViaEnvironmentVariable()
        {
            foreach (string value in new[] { "1", "true" })
            {
                Environment.SetEnvironmentVariable(DebugPreservation.EnvironmentVariable, value);
                Assert.IsTrue(DebugPreservation.ReleaseEnabled(), $"value '{value}' must enable preservation");
            }
        }

        [TestMethod]
        public void IsEnabled_ReleaseBuild_IgnoresUnrelatedValues()
        {
            foreach (string value in new[] { "0", "false", "yes", "" })
            {
                Environment.SetEnvironmentVariable(DebugPreservation.EnvironmentVariable, value);
                Assert.IsFalse(DebugPreservation.ReleaseEnabled(), $"value '{value}' must not enable preservation");
            }
        }

        [TestMethod]
        public async Task DisableSsh_PreservationSkipsRestore()
        {
            // With preservation on, the step must not touch sshd at all —
            // not even the marker probe: a deployment kept for
            // investigation stays reachable over SSH.
            DebugPreservation.TestOverride = true;
            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.VmName).Returns("vm");

            await new DisableSshStep().ExecuteAsync(
                shell.Object, new GalleryItem(), new VmCustomizations(),
                Mock.Of<ILogger>(), CancellationToken.None);

            shell.Verify(s => s.RunCommandAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task RemoveAutomationUser_PreservationSkipsScheduling()
        {
            // With preservation on, the removal one-shot must never be
            // scheduled: the vmcreate user (and its key) survives the
            // deployment.
            DebugPreservation.TestOverride = true;
            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.VmName).Returns("vm");

            await new RemoveAutomationUserStep().ExecuteAsync(
                shell.Object, new GalleryItem(), new VmCustomizations(),
                Mock.Of<ILogger>(), CancellationToken.None);

            shell.Verify(s => s.RunCommandAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task Steps_ReleaseBehavior_RestoresAndRemoves()
        {
            // The release contract: without preservation, SSH state is
            // restored and the automation user removal is scheduled.
            DebugPreservation.TestOverride = false;
            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.VmName).Returns("vm");
            shell.SetupSequence(s => s.RunCommandAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("RESTORE\n")
                .ReturnsAsync("ok")
                .ReturnsAsync("EXISTS\n")
                .ReturnsAsync("ok");

            await new DisableSshStep().ExecuteAsync(
                shell.Object, new GalleryItem(), new VmCustomizations(),
                Mock.Of<ILogger>(), CancellationToken.None);
            await new RemoveAutomationUserStep().ExecuteAsync(
                shell.Object, new GalleryItem(), new VmCustomizations(),
                Mock.Of<ILogger>(), CancellationToken.None);

            shell.Verify(s => s.RunCommandAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }
    }
}
