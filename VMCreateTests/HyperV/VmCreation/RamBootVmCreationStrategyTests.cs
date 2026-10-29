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
    public sealed class RamBootVmCreationStrategyTests
    {
        // Fixture fields — assigned in [TestInitialize] Setup() before every test.
        private Mock<IVmLifecycleManager> _lifecycleManager = null!;
        private Mock<IVmDiskManager> _diskManager = null!;
        private Mock<IVmBootManager> _bootManager = null!;
        private Mock<IVmNetworkManager> _networkManager = null!;
        private Mock<IVmConfigManager> _configManager = null!;
        private RamBootVmCreationStrategy _strategy = null!;
        private VmSettings _vmSettings = null!;
        private VmDeploymentPlan _plan = null!;
        private VmCustomizations _customizations = null!;
        private GalleryItem _item = null!;
        private string _isoPath = null!;

        [TestInitialize]
        public void Setup()
        {
            _lifecycleManager = new Mock<IVmLifecycleManager>();
            _diskManager = new Mock<IVmDiskManager>();
            _bootManager = new Mock<IVmBootManager>();
            _networkManager = new Mock<IVmNetworkManager>();
            _configManager = new Mock<IVmConfigManager>();

            _vmSettings = new VmSettings { VMName = "TestVM", MemoryInMB = 4096 };
            _plan = VmDeploymentPlan.FromSettings(_vmSettings);
            _customizations = new VmCustomizations();
            _item = new GalleryItem { Name = "Fatdog64", IsWindows = false, IsDiskless = true };
            _isoPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".iso");
            File.WriteAllBytes(_isoPath, new byte[0]);

            _strategy = new RamBootVmCreationStrategy(
                _lifecycleManager.Object,
                _bootManager.Object,
                _networkManager.Object,
                _configManager.Object,
                new Mock<ILogger<RamBootVmCreationStrategy>>().Object,
                Mock.Of<IVmPathService>(s =>
                    s.DefaultVmPath == @"C:\Hyper-V" &&
                    s.DefaultVhdxPath == @"C:\Hyper-V\Virtual hard disks"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { File.Delete(_isoPath); } catch { }
        }

        [TestMethod]
        public void CanHandle_ReturnsTrue_ForDisklessIso()
        {
            Assert.IsTrue(_strategy.CanHandle(_item, DiskImageFormat.Iso));
        }

        [TestMethod]
        public void CanHandle_ReturnsFalse_ForNonDisklessIso()
        {
            // Non-diskless ISOs must fall through to IsoVmCreationStrategy,
            // which installs to a VHDX.
            var installer = new GalleryItem { Name = "Rocky", IsDiskless = false };

            Assert.IsFalse(_strategy.CanHandle(installer, DiskImageFormat.Iso));
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
        public async Task CreateVMAsync_NeverCreatesHardDrive()
        {
            // The whole point of RAM boot: no VHDX is ever created or attached.
            await ExecuteAsync();

            _diskManager.Verify(h => h.AddNewHardDrive(It.IsAny<VmDeploymentPlan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
