using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;
using VMCreate.HyperV.VmCreation;
using VMCreate.MediaHandlers;

namespace VMCreate.Tests.HyperV.VmCreation
{
    [TestClass]
    public sealed class DiskImageVmCreationStrategyTests
    {
        private Mock<IVmLifecycleManager> _lifecycleManager;
        private Mock<IVmDiskManager> _diskManager;
        private Mock<IVmBootManager> _bootManager;
        private Mock<IVmNetworkManager> _networkManager;
        private Mock<IVmConfigManager> _configManager;
        private Mock<IGuestShellFactory> _guestShellFactory;
        private Mock<ISshKeyManager> _sshKeyManager;
        private Mock<IKvpSender> _kvpSender;
        private Mock<IKvpPoller> _kvpPoller;
        private Mock<IVmShutdownWatcher> _shutdownWatcher;
        private Mock<IGuestDiagnosticsCollector> _diagnosticsCollector;
        private Mock<IPostBootCustomizationService> _postBootService;
        private Mock<IHostNetworkService> _hostNetworkService;
        private Mock<IIsoBootCycleRunner> _isoBootRunner;
        private DiskImageVmCreationStrategy _strategy;
        private VmSettings _vmSettings;
        private VmDeploymentPlan _plan;
        private VmCustomizations _customizations;
        private GalleryItem _item;
        private string _mediaPath;
        private string _cloningIsoPath;

        [TestInitialize]
        public void Setup()
        {
            _lifecycleManager = new Mock<IVmLifecycleManager>();
            _diskManager = new Mock<IVmDiskManager>();
            _bootManager = new Mock<IVmBootManager>();
            _networkManager = new Mock<IVmNetworkManager>();
            _configManager = new Mock<IVmConfigManager>();
            _guestShellFactory = new Mock<IGuestShellFactory>();
            _sshKeyManager = new Mock<ISshKeyManager>();
            _kvpSender = new Mock<IKvpSender>();
            _kvpPoller = new Mock<IKvpPoller>();
            _shutdownWatcher = new Mock<IVmShutdownWatcher>();
            _diagnosticsCollector = new Mock<IGuestDiagnosticsCollector>();
            _postBootService = new Mock<IPostBootCustomizationService>();
            _hostNetworkService = new Mock<IHostNetworkService>();
            _isoBootRunner = new Mock<IIsoBootCycleRunner>();

            _vmSettings = new VmSettings
            {
                VMName = "TestVM",
                MemoryInMB = 4096,
                CloningIsoPath = Path.Combine(Path.GetTempPath(), $"clone_{Guid.NewGuid():N}.iso")
            };
            _plan = VmDeploymentPlan.FromSettings(_vmSettings);
            _cloningIsoPath = _plan.CloningIsoPath;
            _customizations = new VmCustomizations { ConfigureXrdp = true };
            _item = new GalleryItem { Name = "TestImage", IsNativeHyperV = false, IsWindows = false };
            _mediaPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vhdx");
            File.WriteAllBytes(_mediaPath, new byte[0]);
            File.WriteAllBytes(_cloningIsoPath, new byte[0]);

            _isoBootRunner.Setup(r => r.RunAsync(
                It.IsAny<VmCreationContext>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<VmCustomizations>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(IsoBootCycleResult.Succeeded());

            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.WaitForReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _guestShellFactory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<string>())).Returns(shell.Object);
            _sshKeyManager.Setup(s => s.GetPrivateKeyPath(It.IsAny<string>())).Returns("C:\\Users\\Test\\.ssh\\id_rsa");

            _strategy = new DiskImageVmCreationStrategy(
                _lifecycleManager.Object,
                _diskManager.Object,
                _bootManager.Object,
                _networkManager.Object,
                _configManager.Object,
                _guestShellFactory.Object,
                _sshKeyManager.Object,
                _kvpSender.Object,
                _kvpPoller.Object,
                _shutdownWatcher.Object,
                _diagnosticsCollector.Object,
                _postBootService.Object,
                _hostNetworkService.Object,
                new Mock<ILogger<DiskImageVmCreationStrategy>>().Object,
                Mock.Of<IVmPathService>(s => s.DefaultVmPath == @"C:\Hyper-V" && s.DefaultVhdxPath == @"C:\Hyper-V\Virtual hard disks"),
                _isoBootRunner.Object);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { File.Delete(_mediaPath); } catch { }
            try { File.Delete(_cloningIsoPath); } catch { }
        }

        [TestMethod]
        public void CanHandle_ReturnsTrue_ForVmdk()
        {
            Assert.IsTrue(_strategy.CanHandle(_item, DiskImageFormat.Vmdk));
        }

        [TestMethod]
        public void CanHandle_ReturnsTrue_ForQcow2()
        {
            Assert.IsTrue(_strategy.CanHandle(_item, DiskImageFormat.Qcow2));
        }

        [TestMethod]
        public void CanHandle_ReturnsFalse_ForNativeHyperV()
        {
            Assert.IsFalse(_strategy.CanHandle(new GalleryItem { IsNativeHyperV = true }, DiskImageFormat.Vhdx));
        }

        [TestMethod]
        public void CanHandle_ReturnsFalse_ForIso()
        {
            Assert.IsFalse(_strategy.CanHandle(_item, DiskImageFormat.Iso));
        }

        [TestMethod]
        public async Task CreateVMAsync_Gen2_AttachesExistingDiskAndBootDvd()
        {
            await ExecuteAsync(generation: 2);

            _lifecycleManager.Verify(h => h.CreateVMAsync(_plan, @"C:\Hyper-V", 2, It.IsAny<CancellationToken>()), Times.Once);
            _diskManager.Verify(h => h.AddExistingHardDrive(_plan, _mediaPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.AddBootDvd(_plan, _cloningIsoPath, It.IsAny<CancellationToken>()), Times.Once);
            _isoBootRunner.Verify(r => r.RunAsync(
                It.IsAny<VmCreationContext>(), 2, _mediaPath, _customizations,
                It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_Gen2_NoIsoBoot_WhenNoCustomizationNeeded()
        {
            _customizations.ConfigureXrdp = false;

            await ExecuteAsync(generation: 2);

            _isoBootRunner.Verify(r => r.RunAsync(
                It.IsAny<VmCreationContext>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<VmCustomizations>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>()), Times.Never);
            _bootManager.Verify(h => h.AddBootDvd(_plan, _cloningIsoPath, It.IsAny<CancellationToken>()), Times.Never);
            _bootManager.Verify(h => h.SetFirstBootToHardDrive(_plan, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_Gen1_AddsNewAndExistingHardDrives()
        {
            await ExecuteAsync(generation: 1);

            _diskManager.Verify(h => h.AddNewHardDrive(_plan, @"C:\Hyper-V\Virtual hard disks", It.IsAny<CancellationToken>()), Times.Once);
            _diskManager.Verify(h => h.AddExistingHardDrive(_plan, _mediaPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.SetFirstBootToDvd(_plan, It.IsAny<CancellationToken>()), Times.Once);
            _isoBootRunner.Verify(r => r.RunAsync(
                It.IsAny<VmCreationContext>(), 1, _mediaPath, _customizations,
                It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_IsoBootFailure_Throws()
        {
            _isoBootRunner.Setup(r => r.RunAsync(
                It.IsAny<VmCreationContext>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<VmCustomizations>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(IsoBootCycleResult.Failed("mock timeout", "mock diagnostics"));

            bool threw = false;
            try
            {
                await ExecuteAsync(generation: 2);
            }
            catch (Exception ex) when (ex.Message.Contains("ISO customization failed"))
            {
                threw = true;
            }

            Assert.IsTrue(threw);
        }

        private async Task ExecuteAsync(int generation)
        {
            var context = new VmCreationContext(
                _plan,
                _customizations,
                _mediaPath,
                _item,
                new MediaPreparationResult(_mediaPath, generation),
                CancellationToken.None,
                new Progress<CreateVMProgressInfo>());

            await _strategy.CreateVMAsync(context);
        }
    }
}
