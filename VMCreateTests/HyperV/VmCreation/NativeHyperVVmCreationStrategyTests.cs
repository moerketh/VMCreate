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
    public sealed class NativeHyperVVmCreationStrategyTests
    {
        private Mock<IVmLifecycleManager> _lifecycleManager;
        private Mock<IVmDiskManager> _diskManager;
        private Mock<IVmBootManager> _bootManager;
        private Mock<IVmNetworkManager> _networkManager;
        private Mock<IVmConfigManager> _configManager;
        private Mock<IGuestShellFactory> _guestShellFactory;
        private Mock<IUnattendInjector> _unattendInjector;
        private Mock<IPostBootCustomizationService> _postBootService;
        private NativeHyperVVmCreationStrategy _strategy;
        private VmSettings _vmSettings;
        private VmDeploymentPlan _plan;
        private VmCustomizations _customizations;
        private GalleryItem _item;
        private string _mediaPath;

        [TestInitialize]
        public void Setup()
        {
            _lifecycleManager = new Mock<IVmLifecycleManager>();
            _diskManager = new Mock<IVmDiskManager>();
            _bootManager = new Mock<IVmBootManager>();
            _networkManager = new Mock<IVmNetworkManager>();
            _configManager = new Mock<IVmConfigManager>();
            _guestShellFactory = new Mock<IGuestShellFactory>();
            _unattendInjector = new Mock<IUnattendInjector>();
            _postBootService = new Mock<IPostBootCustomizationService>();

            _vmSettings = new VmSettings { VMName = "TestVM", MemoryInMB = 4096 };
            _plan = VmDeploymentPlan.FromSettings(_vmSettings);
            _customizations = new VmCustomizations();
            _item = new GalleryItem { Name = "TestImage", IsNativeHyperV = true };
            _mediaPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vhdx");

            File.WriteAllBytes(_mediaPath, new byte[0]);

            _strategy = new NativeHyperVVmCreationStrategy(
                _lifecycleManager.Object,
                _diskManager.Object,
                _bootManager.Object,
                _networkManager.Object,
                _configManager.Object,
                _guestShellFactory.Object,
                _unattendInjector.Object,
                _postBootService.Object,
                new Mock<ILogger<NativeHyperVVmCreationStrategy>>().Object,
                Mock.Of<IVmPathService>(s => s.DefaultVmPath == @"C:\Hyper-V"));

            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.WaitForReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _guestShellFactory.Setup(f => f.CreateForWindows(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(shell.Object);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { File.Delete(_mediaPath); } catch { }
        }

        [TestMethod]
        public void CanHandle_ReturnsTrue_WhenNativeHyperV()
        {
            Assert.IsTrue(_strategy.CanHandle(new GalleryItem { IsNativeHyperV = true }, DiskImageFormat.Vhdx));
        }

        [TestMethod]
        public void CanHandle_ReturnsFalse_WhenNotNativeHyperV()
        {
            Assert.IsFalse(_strategy.CanHandle(new GalleryItem { IsNativeHyperV = false }, DiskImageFormat.Vhdx));
        }

        [TestMethod]
        public async Task CreateVMAsync_CreatesVmSkeleton_Windows()
        {
            _item.IsWindows = true;
            _unattendInjector.Setup(u => u.InjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            await ExecuteAsync();

            _lifecycleManager.Verify(h => h.CreateVMAsync(_plan, @"C:\Hyper-V", 2, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_ClearsReadOnlyFlag_Windows()
        {
            _item.IsWindows = true;
            _unattendInjector.Setup(u => u.InjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            File.SetAttributes(_mediaPath, FileAttributes.ReadOnly);

            await ExecuteAsync();

            Assert.IsFalse(File.GetAttributes(_mediaPath).HasFlag(FileAttributes.ReadOnly));
        }

        [TestMethod]
        public async Task CreateVMAsync_AttachesExistingHardDrive()
        {
            await ExecuteAsync();

            _diskManager.Verify(h => h.AddExistingHardDrive(_plan, _mediaPath, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_SetsBootToHardDrive()
        {
            await ExecuteAsync();

            _bootManager.Verify(h => h.SetFirstBootToHardDrive(_plan, It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_Throws_WhenUnattendInjectionFails_Windows()
        {
            _item.IsWindows = true;
            _unattendInjector.Setup(u => u.InjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            bool threw = false;
            try
            {
                await ExecuteAsync();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.IsTrue(threw, "Expected InvalidOperationException when unattend injection fails.");
        }

        [TestMethod]
        public async Task CreateVMAsync_DoesNotInjectUnattend_Linux()
        {
            _item.IsWindows = false;

            await ExecuteAsync();

            _unattendInjector.Verify(u => u.InjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateVMAsync_UsesPreparedMediaPath_NotOriginalSourceFile()
        {
            string originalSourceFile = Path.Combine(Path.GetTempPath(), "original-downloaded.vhdx");
            string preparedMediaPath = Path.Combine(Path.GetTempPath(), $"prepared-{Guid.NewGuid():N}.vhdx");
            File.WriteAllBytes(preparedMediaPath, new byte[0]);

            var context = new VmCreationContext(
                _plan,
                _customizations,
                originalSourceFile,
                _item,
                new MediaPreparationResult(preparedMediaPath, 2),
                CancellationToken.None,
                new Progress<CreateVMProgressInfo>());

            await _strategy.CreateVMAsync(context);

            _diskManager.Verify(h => h.AddExistingHardDrive(_plan, preparedMediaPath, It.IsAny<CancellationToken>()), Times.Once);
            _diskManager.Verify(h => h.AddExistingHardDrive(_plan, originalSourceFile, It.IsAny<CancellationToken>()), Times.Never);

            try { File.Delete(preparedMediaPath); } catch { }
        }

        private async Task ExecuteAsync()
        {
            var context = new VmCreationContext(
                _plan,
                _customizations,
                _mediaPath,
                _item,
                new MediaPreparationResult(_mediaPath, 2),
                CancellationToken.None,
                new Progress<CreateVMProgressInfo>());

            await _strategy.CreateVMAsync(context);
        }
    }
}
