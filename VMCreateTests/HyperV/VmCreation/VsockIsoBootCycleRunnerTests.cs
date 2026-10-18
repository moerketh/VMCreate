using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV.VmCreation;
using VMCreate.MediaHandlers;

namespace VMCreate.Tests.HyperV.VmCreation
{
    /// <summary>
    /// Unit tests for the v2 (vsock) ISO boot-cycle runner state flow. The
    /// guest scripts are faked via IVsockGuestScriptRunner; the transport
    /// itself is W1-tested (SshTransportTests) and live-validated.
    /// </summary>
    [TestClass]
    public class VsockIsoBootCycleRunnerTests
    {
        private Mock<IKvpSender> _kvpSender = null!;
        private Mock<IKvpMaintenance> _kvpMaintenance = null!;
        private Mock<IGuestShellFactory> _guestShellFactory = null!;
        private Mock<ISshKeyManager> _sshKeyManager = null!;
        private Mock<IVmShutdownWatcher> _shutdownWatcher = null!;
        private Mock<IGuestDiagnosticsCollector> _diagnosticsCollector = null!;
        private Mock<IVmLifecycleManager> _lifecycleManager = null!;
        private Mock<IVmDiskManager> _diskManager = null!;
        private Mock<IVmBootManager> _bootManager = null!;
        private Mock<IVsockGuestScriptRunner> _scriptRunner = null!;
        private Mock<IGuestShell> _shell = null!;

        private VmDeploymentPlan _plan = null!;
        private VmCustomizations _customizations = null!;
        private GalleryItem _item = null!;
        private VsockIsoBootCycleRunner _runner = null!;

        [TestInitialize]
        public void Setup()
        {
            _kvpSender = new Mock<IKvpSender>();
            _kvpMaintenance = new Mock<IKvpMaintenance>();
            _guestShellFactory = new Mock<IGuestShellFactory>();
            _sshKeyManager = new Mock<ISshKeyManager>();
            _shutdownWatcher = new Mock<IVmShutdownWatcher>();
            _diagnosticsCollector = new Mock<IGuestDiagnosticsCollector>();
            _lifecycleManager = new Mock<IVmLifecycleManager>();
            _diskManager = new Mock<IVmDiskManager>();
            _bootManager = new Mock<IVmBootManager>();
            _scriptRunner = new Mock<IVsockGuestScriptRunner>();
            _shell = new Mock<IGuestShell>();

            _plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM" });
            _customizations = new VmCustomizations();
            _item = new GalleryItem { Name = "TestImage", IsNativeHyperV = false, IsWindows = false };

            _sshKeyManager
                .Setup(m => m.EnsureKeyPairAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync("ssh-ed25519 AAAATESTKEY vmcreate");
            _sshKeyManager
                .Setup(m => m.GetPrivateKeyPath(It.IsAny<string?>()))
                .Returns(@"C:\tmp\test-key");
            _kvpMaintenance
                .Setup(m => m.ResolveVmGuidAsync("TestVM", It.IsAny<CancellationToken>()))
                .ReturnsAsync("d811e738-5c46-4f57-a171-4f822a38f22a");
            _guestShellFactory
                .Setup(m => m.CreateVsock("TestVM", It.IsAny<string>(), It.IsAny<string>()))
                .Returns(_shell.Object);
            _scriptRunner
                .Setup(m => m.RunAsync(It.IsAny<IGuestShell>(), It.IsAny<string>(), It.IsAny<VmCreationContext>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(IsoBootCycleResult.Succeeded());
            _shutdownWatcher
                .Setup(m => m.WaitForVMShutdownAsync("TestVM", It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(true);

            _runner = new VsockIsoBootCycleRunner(
                _kvpSender.Object,
                _kvpMaintenance.Object,
                _guestShellFactory.Object,
                _sshKeyManager.Object,
                _shutdownWatcher.Object,
                _diagnosticsCollector.Object,
                _lifecycleManager.Object,
                _diskManager.Object,
                _bootManager.Object,
                _scriptRunner.Object,
                Mock.Of<ILogger<VsockIsoBootCycleRunner>>());
        }

        private VmCreationContext CreateContext() =>
            new VmCreationContext(
                _plan,
                _customizations,
                "source.vmdk",
                _item,
                new MediaPreparationResult("media.vhdx", 2),
                CancellationToken.None,
                new Progress<CreateVMProgressInfo>());

        private Task<IsoBootCycleResult> RunAsync(int generation) =>
            _runner.RunAsync(CreateContext(), generation, "media.vhdx", _customizations,
                new Progress<CreateVMProgressInfo>(), CancellationToken.None);

        [TestMethod]
        public async Task CustomizeFlow_SendsKey_Connects_RunsScripts_PowersOff_CleansUpKvp()
        {
            var result = await RunAsync(2);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            // Key sent via KVP before anything else.
            _kvpSender.Verify(m => m.SendKVPToGuestAsync("TestVM", "VMCREATE_SSH_PUBKEY", "ssh-ed25519 AAAATESTKEY vmcreate", It.IsAny<CancellationToken>()), Times.Once);
            // Vsock shell created with the resolved GUID.
            _guestShellFactory.Verify(m => m.CreateVsock("TestVM", @"C:\tmp\test-key", "d811e738-5c46-4f57-a171-4f822a38f22a"), Times.Once);
            _shell.Verify(m => m.WaitForReadyAsync(It.IsAny<CancellationToken>()), Times.Once);
            // Customize scenario for generation 2.
            _scriptRunner.Verify(m => m.RunAsync(It.IsAny<IGuestShell>(), "customize", It.IsAny<VmCreationContext>(), It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
            // Poweroff + shutdown confirm + boot finalized.
            _shell.Verify(m => m.RunCommandAsync("sudo systemctl --no-block poweroff", It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(m => m.RemoveBootDvd(It.IsAny<VmDeploymentPlan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(m => m.SetFirstBootToHardDrive(It.IsAny<VmDeploymentPlan>(), It.IsAny<CancellationToken>()), Times.Once);
            // KVP item removed on the success path too.
            _kvpMaintenance.Verify(m => m.RemoveKvpItemAsync("TestVM", "VMCREATE_SSH_PUBKEY", "ssh-ed25519 AAAATESTKEY vmcreate", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CloneFlow_Generation1_RunsCloneScenario_AndRemovesMbrSourceDisk()
        {
            var result = await RunAsync(1);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            _scriptRunner.Verify(m => m.RunAsync(It.IsAny<IGuestShell>(), "clone", It.IsAny<VmCreationContext>(), It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
            // MBR source detached at the shared SCSI location.
            _diskManager.Verify(m => m.RemoveHardDrive(It.IsAny<VmDeploymentPlan>(), IsoBootCycleRunner.MbrSourceDiskScsiControllerLocation, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task PoweroffNonTransportError_FailsTheDeploy()
        {
            // sudo/systemctl exiting 1 is a REAL failure — swallowing it
            // would wait out the shutdown budget, force-stop, and report a
            // false success.
            _shell
                .Setup(m => m.RunCommandAsync("sudo systemctl --no-block poweroff", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("SSH command failed (exit code 1): Failed to start poweroff.target: refused"));

            try
            {
                await RunAsync(2);
                Assert.Fail("Expected the poweroff failure to propagate");
            }
            catch (Exception ex)
            {
                StringAssert.Contains(ex.Message, "refused");
            }
            // Whole-body catch still stopped the VM.
            _lifecycleManager.Verify(m => m.StopVMAsync("TestVM", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task GuidResolutionFailure_StopsTheVm()
        {
            _kvpMaintenance
                .Setup(m => m.ResolveVmGuidAsync("TestVM", It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            try
            {
                await RunAsync(2);
                Assert.Fail("Expected InvalidOperationException");
            }
            catch (InvalidOperationException ex)
            {
                StringAssert.Contains(ex.Message, "GUID");
            }
            // Pre-shell failure: VM stopped, no shell existed for logs.
            _lifecycleManager.Verify(m => m.StopVMAsync("TestVM", It.IsAny<CancellationToken>()), Times.Once);
            _scriptRunner.Verify(m => m.RunAsync(It.IsAny<IGuestShell>(), It.IsAny<string>(), It.IsAny<VmCreationContext>(), It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task WaitForReadyThrow_StopsTheVm()
        {
            _shell
                .Setup(m => m.WaitForReadyAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("vsock SSH did not become available on VM 'TestVM' within 120s"));

            try
            {
                await RunAsync(2);
                Assert.Fail("Expected TimeoutException");
            }
            catch (TimeoutException) { /* expected */ }

            _lifecycleManager.Verify(m => m.StopVMAsync("TestVM", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task PoweroffTransportError_IsExpected_DeployStillSucceeds()
        {
            // The guest kills sshd while the poweroff command is in flight:
            // ssh exits 255. That transport error must NOT fail the deploy.
            // The message carries a genuine transport-error phrase so
            // IsSshTransportError classifies it (a bare "exit code 255"
            // would not — by design).
            _shell
                .Setup(m => m.RunCommandAsync("sudo systemctl --no-block poweroff", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("SSH command failed (exit code 255): Connection closed by remote host"));

            var result = await RunAsync(2);

            Assert.IsTrue(result.Success, result.ErrorMessage);
        }

        [TestMethod]
        public async Task ScriptFailure_StopsTheVm_DiagnosticsReachTheResult()
        {
            _scriptRunner
                .Setup(m => m.RunAsync(It.IsAny<IGuestShell>(), It.IsAny<string>(), It.IsAny<VmCreationContext>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(IsoBootCycleResult.Failed("partclone failed"));
            // The live shell returns journal text; the mock must too.
            _shell
                .Setup(m => m.RunCommandAsync(It.Is<string>(c => c.Contains("journalctl")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("journal-line-1\njournal-line-2");

            var result = await RunAsync(2);

            Assert.IsFalse(result.Success);
            // Self-contained failure path: the VM is stopped even when the
            // orchestrator's cleanup is bypassed (debug-preserved runs).
            _lifecycleManager.Verify(m => m.StopVMAsync("TestVM", It.IsAny<CancellationToken>()), Times.Once);
            // Guest logs are attached to the result the GUI renders.
            Assert.IsFalse(string.IsNullOrEmpty(result.DiagnosticsLog), "diagnostics must reach the failed result");
            StringAssert.Contains(result.DiagnosticsLog, "journal-line-1");
        }

        [TestMethod]
        public async Task AddFailure_FallsBackToModify_OriginalAddErrorSurfacesIfBothFail()
        {
            _kvpSender
                .Setup(m => m.SendKVPToGuestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Add failed: JobState 10"));

            // Modify succeeds → deploy proceeds normally.
            var result = await RunAsync(2);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            _kvpMaintenance.Verify(m => m.ModifyKvpItemAsync("TestVM", "VMCREATE_SSH_PUBKEY", "ssh-ed25519 AAAATESTKEY vmcreate", It.IsAny<CancellationToken>()), Times.Once);

            // Modify also fails → the ADD error surfaces (names the real problem).
            _kvpMaintenance
                .Setup(m => m.ModifyKvpItemAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Modify failed: wmi down"));
            try
            {
                await RunAsync(2);
                Assert.Fail("Expected the Add error to surface");
            }
            catch (Exception ex)
            {
                StringAssert.Contains(ex.Message, "Add failed", "the original Add error must surface, not the Modify error");
            }
        }

        [TestMethod]
        public async Task ScriptFailure_StillRemovesKvp_AndDoesNotFinalizeBoot()
        {
            _scriptRunner
                .Setup(m => m.RunAsync(It.IsAny<IGuestShell>(), It.IsAny<string>(), It.IsAny<VmCreationContext>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(IsoBootCycleResult.Failed("partclone failed"));

            var result = await RunAsync(2);

            Assert.IsFalse(result.Success);
            _bootManager.Verify(m => m.RemoveBootDvd(It.IsAny<VmDeploymentPlan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            // Finally-path hygiene: KVP removed even when steps failed.
            _kvpMaintenance.Verify(m => m.RemoveKvpItemAsync("TestVM", "VMCREATE_SSH_PUBKEY", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ThrownException_StillRemovesKvp()
        {
            _scriptRunner
                .Setup(m => m.RunAsync(It.IsAny<IGuestShell>(), It.IsAny<string>(), It.IsAny<VmCreationContext>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("guest exploded"));

            try
            {
                await RunAsync(2);
                Assert.Fail("Expected InvalidOperationException");
            }
            catch (InvalidOperationException) { /* expected */ }

            _kvpMaintenance.Verify(m => m.RemoveKvpItemAsync("TestVM", "VMCREATE_SSH_PUBKEY", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task KvpCleanupFailure_DoesNotMaskOriginalResult()
        {
            _kvpMaintenance
                .Setup(m => m.RemoveKvpItemAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("wmi down"));

            var result = await RunAsync(2);

            // Success stands; the cleanup failure is logged, not thrown.
            Assert.IsTrue(result.Success, result.ErrorMessage);
        }

        [TestMethod]
        public async Task ShutdownTimeout_ForceStops_ButStillSucceeds()
        {
            _shutdownWatcher
                .Setup(m => m.WaitForVMShutdownAsync("TestVM", It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(false);

            var result = await RunAsync(2);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            _lifecycleManager.Verify(m => m.StopVMAsync("TestVM", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task UnresolvableGuid_StillCleansKvp()
        {
            _kvpMaintenance
                .Setup(m => m.ResolveVmGuidAsync("TestVM", It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            try
            {
                await RunAsync(2);
                Assert.Fail("Expected InvalidOperationException");
            }
            catch (InvalidOperationException) { /* expected */ }

            _kvpMaintenance.Verify(m => m.RemoveKvpItemAsync("TestVM", "VMCREATE_SSH_PUBKEY", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        #region Protocol parser

        [TestMethod]
        public void Parser_AcceptsProgressLine_WithMessage()
        {
            bool ok = VsockIsoBootCycleRunner.TryParseProgressLine("VMCREATE:PROGRESS:42:cloning root", out int pct, out string msg);
            Assert.IsTrue(ok);
            Assert.AreEqual(42, pct);
            Assert.AreEqual("cloning root", msg);
        }

        [TestMethod]
        public void Parser_PreservesColonsInMessage()
        {
            // The point of the 4-way split: a message containing colons
            // survives intact instead of being truncated at the first one.
            bool ok = VsockIsoBootCycleRunner.TryParseProgressLine("VMCREATE:PROGRESS:7:error at 12:34:56 in phase", out int pct, out string msg);
            Assert.IsTrue(ok);
            Assert.AreEqual(7, pct);
            Assert.AreEqual("error at 12:34:56 in phase", msg);
        }

        [TestMethod]
        public void Parser_RejectsOutOfBoundsAndGarbage()
        {
            Assert.IsFalse(VsockIsoBootCycleRunner.TryParseProgressLine("VMCREATE:PROGRESS:142:overflow", out _, out _));
            Assert.IsFalse(VsockIsoBootCycleRunner.TryParseProgressLine("VMCREATE:PROGRESS:notanumber:x", out _, out _));
            Assert.IsFalse(VsockIsoBootCycleRunner.TryParseProgressLine("random stdout line", out _, out _));
            Assert.IsFalse(VsockIsoBootCycleRunner.TryParseProgressLine("", out _, out _));
            Assert.IsFalse(VsockIsoBootCycleRunner.TryParseProgressLine(null, out _, out _));
        }

        [TestMethod]
        public void Parser_MessageLine_WithAndWithoutColons()
        {
            Assert.IsTrue(VsockIsoBootCycleRunner.TryParseMessageLine("VMCREATE:MSG:installing xrdp", out string m1));
            Assert.AreEqual("installing xrdp", m1);
            Assert.IsTrue(VsockIsoBootCycleRunner.TryParseMessageLine("VMCREATE:MSG:waiting for 10:30 timeout", out string m2));
            Assert.AreEqual("waiting for 10:30 timeout", m2);
            Assert.IsFalse(VsockIsoBootCycleRunner.TryParseMessageLine("VMCREATE:PROGRESS:1:x", out _));
        }

        #endregion
    }
}
