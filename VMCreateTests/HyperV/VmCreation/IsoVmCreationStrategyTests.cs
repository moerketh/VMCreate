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
    public sealed class IsoVmCreationStrategyTests
    {
        private Mock<IVmLifecycleManager> _lifecycleManager;
        private Mock<IVmDiskManager> _diskManager;
        private Mock<IVmBootManager> _bootManager;
        private Mock<IVmNetworkManager> _networkManager;
        private Mock<IVmConfigManager> _configManager;
        private Mock<IGuestShellFactory> _guestShellFactory;
        private Mock<IPostBootCustomizationService> _postBootService;
        private IsoVmCreationStrategy _strategy;
        private VmSettings _vmSettings;
        private VmDeploymentPlan _plan;
        private VmCustomizations _customizations;
        private GalleryItem _item;
        private string _isoPath;

        [TestInitialize]
        public void Setup()
        {
            _lifecycleManager = new Mock<IVmLifecycleManager>();
            _diskManager = new Mock<IVmDiskManager>();
            _bootManager = new Mock<IVmBootManager>();
            _networkManager = new Mock<IVmNetworkManager>();
            _configManager = new Mock<IVmConfigManager>();
            _guestShellFactory = new Mock<IGuestShellFactory>();
            _postBootService = new Mock<IPostBootCustomizationService>();

            _vmSettings = new VmSettings { VMName = "TestVM", MemoryInMB = 4096 };
            _plan = VmDeploymentPlan.FromSettings(_vmSettings);
            _customizations = new VmCustomizations();
            _item = new GalleryItem { Name = "TestISO", IsWindows = false };
            _isoPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".iso");
            File.WriteAllBytes(_isoPath, new byte[0]);

            _strategy = new IsoVmCreationStrategy(
                _lifecycleManager.Object,
                _diskManager.Object,
                _bootManager.Object,
                _networkManager.Object,
                _configManager.Object,
                _guestShellFactory.Object,
                _postBootService.Object,
                new Mock<ILogger<IsoVmCreationStrategy>>().Object,
                Mock.Of<IVmPathService>(s =>
                    s.DefaultVmPath == @"C:\Hyper-V" &&
                    s.DefaultVhdxPath == @"C:\Hyper-V\Virtual hard disks"));

            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.WaitForReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _guestShellFactory.Setup(f => f.CreateForWindows(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(shell.Object);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { File.Delete(_isoPath); } catch { }
        }

        [TestMethod]
        public void CanHandle_ReturnsTrue_ForIso()
        {
            Assert.IsTrue(_strategy.CanHandle(_item, DiskImageFormat.Iso));
        }

        [TestMethod]
        public void CanHandle_ReturnsFalse_ForVhdx()
        {
            Assert.IsFalse(_strategy.CanHandle(_item, DiskImageFormat.Vhdx));
        }

        [TestMethod]
        public async Task CreateVMAsync_CreatesVmSkeleton()
        {
            await ExecuteAsync();

            _lifecycleManager.Verify(h => h.CreateVMAsync(_plan, @"C:\Hyper-V", 2, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_AddsNewHardDrive()
        {
            await ExecuteAsync();

            _diskManager.Verify(h => h.AddNewHardDrive(_plan, @"C:\Hyper-V\Virtual hard disks", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_AddsBootDvdWithIsoPath()
        {
            await ExecuteAsync();

            _bootManager.Verify(h => h.AddBootDvd(_plan, _isoPath, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_SetsFirstBootToDvd()
        {
            await ExecuteAsync();

            _bootManager.Verify(h => h.SetFirstBootToDvd(_plan, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_StartsVm()
        {
            await ExecuteAsync();

            _lifecycleManager.Verify(h => h.StartVM(_plan, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_ConnectsNetworkAdapter()
        {
            await ExecuteAsync();
            _networkManager.Verify(h => h.ConnectNetworkAdapter(_plan, It.IsAny<CancellationToken>()), Times.Once);
        }

        private async Task ExecuteAsync()
        {
            var context = new VmCreationContext(
                _plan,
                _customizations,
                _isoPath,
                _item,
                new MediaPreparationResult(_isoPath, 2),
                CancellationToken.None,
                new Progress<CreateVMProgressInfo>());

            await _strategy.CreateVMAsync(context);
        }
    }
}
