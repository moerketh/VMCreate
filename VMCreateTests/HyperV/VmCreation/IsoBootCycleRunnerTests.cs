using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using CreateVM.HyperV.vmbus;
using VMCreate;
using VMCreate.HyperV.VmCreation;

using VMCreate.MediaHandlers;

namespace VMCreate.Tests.HyperV.VmCreation
{
    [TestClass]
    public sealed class IsoBootCycleRunnerTests
    {
        private Mock<IKvpSender> _kvpSender;
        private Mock<IKvpPoller> _kvpPoller;
        private Mock<IVmShutdownWatcher> _shutdownWatcher;
        private Mock<IGuestDiagnosticsCollector> _diagnosticsCollector;
        private Mock<ISshKeyManager> _sshKeyManager;
        private Mock<IHostNetworkService> _hostNetworkService;
        private Mock<IVmLifecycleManager> _lifecycleManager;
        private Mock<IVmDiskManager> _diskManager;
        private Mock<IVmBootManager> _bootManager;
        private IsoBootCycleRunner _runner;
        private VmDeploymentPlan _plan;
        private VmCustomizations _customizations;
        private GalleryItem _item;

        [TestInitialize]
        public void Setup()
        {
            _kvpSender = new Mock<IKvpSender>();
            _kvpPoller = new Mock<IKvpPoller>();
            _shutdownWatcher = new Mock<IVmShutdownWatcher>();
            _diagnosticsCollector = new Mock<IGuestDiagnosticsCollector>();
            _sshKeyManager = new Mock<ISshKeyManager>();
            _hostNetworkService = new Mock<IHostNetworkService>();
            _lifecycleManager = new Mock<IVmLifecycleManager>();
            _diskManager = new Mock<IVmDiskManager>();
            _bootManager = new Mock<IVmBootManager>();

            _plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM" });
            _customizations = new VmCustomizations();
            _item = new GalleryItem { Name = "TestImage", IsNativeHyperV = false, IsWindows = false };

            _runner = new IsoBootCycleRunner(
                _kvpSender.Object,
                _kvpPoller.Object,
                _shutdownWatcher.Object,
                _diagnosticsCollector.Object,
                _sshKeyManager.Object,
                _hostNetworkService.Object,
                _lifecycleManager.Object,
                _diskManager.Object,
                _bootManager.Object,
                new Mock<ILogger<IsoBootCycleRunner>>().Object);
        }

        [TestMethod]
        public async Task RunAsync_Gen2_ShutdownConfirmed_FinalizesBoot()
        {
            _kvpPoller.Setup(p => p.WaitForShutdownWithProgressAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>()))
                .ReturnsAsync(true);

            var result = await RunAsync(generation: 2);

            Assert.IsTrue(result.Success);
            _bootManager.Verify(b => b.RemoveBootDvd(_plan, _plan.CloningIsoPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(b => b.SetFirstBootToHardDrive(_plan, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task RunAsync_Gen1_CloneMarkerSeen_CleansMbrDisk()
        {
            _kvpPoller.Setup(p => p.PollKVPForProgressAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>()))
                .ReturnsAsync(true);
            _kvpPoller.Setup(p => p.WaitForShutdownWithProgressAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>()))
                .ReturnsAsync(true);

            var result = await RunAsync(generation: 1);

            Assert.IsTrue(result.Success);
            _diskManager.Verify(d => d.RemoveHardDrive(_plan, 1, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(b => b.SetFirstBootToHardDrive(_plan, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task RunAsync_Gen1_NoCloneMarker_UsesShutdownWatcher()
        {
            _kvpPoller.Setup(p => p.PollKVPForProgressAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>()))
                .ReturnsAsync(false);
            _shutdownWatcher.Setup(s => s.WaitForVMShutdownAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>()))
                .ReturnsAsync(true);

            var result = await RunAsync(generation: 1);

            Assert.IsTrue(result.Success);
        }

        [TestMethod]
        public async Task RunAsync_Timeout_CollectsDiagnosticsAndFails()
        {
            _kvpPoller.Setup(p => p.WaitForShutdownWithProgressAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>()))
                .ReturnsAsync(false);
            _diagnosticsCollector.Setup(d => d.CollectAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string>()))
                .ReturnsAsync(new GuestDiagnostics { Summary = "timeout summary", RawOutput = "raw output" });

            var result = await RunAsync(generation: 2);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.ErrorMessage.Contains("timeout summary"));
            Assert.AreEqual("raw output", result.DiagnosticsLog);
            _lifecycleManager.Verify(l => l.StopVMAsync("TestVM", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task RunAsync_SendsSshKeyAndNameservers()
        {
            _sshKeyManager.Setup(s => s.EnsureKeyPairAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync("ssh-rsa KEY");
            _hostNetworkService.Setup(h => h.ResolveHostDnsServers()).Returns("192.168.1.1");
            _kvpPoller.Setup(p => p.WaitForShutdownWithProgressAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>()))
                .ReturnsAsync(true);

            await RunAsync(generation: 2);

            _kvpSender.Verify(k => k.SendKVPToGuestAsync("TestVM", "VMCREATE_SSH_PUBKEY", "ssh-rsa KEY", It.IsAny<CancellationToken>()), Times.Once);
            _kvpSender.Verify(k => k.SendKVPToGuestAsync("TestVM", "VMCREATE_NAMESERVERS", "192.168.1.1", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task RunAsync_XrdpEnabled_SendsXrdpFlag()
        {
            _customizations.ConfigureXrdp = true;
            _kvpPoller.Setup(p => p.WaitForShutdownWithProgressAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>()))
                .ReturnsAsync(true);

            await RunAsync(generation: 2);

            _kvpSender.Verify(k => k.SendKVPToGuestAsync("TestVM", "VMCREATE_XRDP", "true", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task RunAsync_Gen2_SendsCustomizeMode()
        {
            _kvpPoller.Setup(p => p.WaitForShutdownWithProgressAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>()))
                .ReturnsAsync(true);

            await RunAsync(generation: 2);

            _kvpSender.Verify(k => k.SendKVPToGuestAsync("TestVM", "VMCREATE_MODE", "customize", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task RunAsync_NullContext_ThrowsArgumentNullException()
        {
            bool threw = false;
            try
            {
                await _runner.RunAsync(
                    null,
                    2,
                    "media.vhdx",
                    _customizations,
                    new Progress<CreateVMProgressInfo>(),
                    CancellationToken.None);
            }
            catch (ArgumentNullException ex) when (ex.ParamName == "context")
            {
                threw = true;
            }

            Assert.IsTrue(threw);
        }

        [TestMethod]
        public async Task RunAsync_NullCustomizations_ThrowsArgumentNullException()
        {
            bool threw = false;
            try
            {
                await _runner.RunAsync(
                    CreateContext(),
                    2,
                    "media.vhdx",
                    null,
                    new Progress<CreateVMProgressInfo>(),
                    CancellationToken.None);
            }
            catch (ArgumentNullException ex) when (ex.ParamName == "customizations")
            {
                threw = true;
            }

            Assert.IsTrue(threw);
        }

        private VmCreationContext CreateContext()
        {
            return new VmCreationContext(
                _plan,
                _customizations,
                "source.vmdk",
                _item,
                new MediaPreparationResult("media.vhdx", 2),
                CancellationToken.None,
                new Progress<CreateVMProgressInfo>());
        }

        private async Task<IsoBootCycleResult> RunAsync(int generation)
        {
            var context = CreateContext();
            return await _runner.RunAsync(
                context,
                generation,
                context.MediaResult.FinalMediaPath,
                _customizations,
                new Progress<CreateVMProgressInfo>(),
                CancellationToken.None);
        }
    }
}
