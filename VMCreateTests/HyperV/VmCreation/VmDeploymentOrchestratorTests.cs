using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;
using VMCreate.HyperV.VmCreation;
using VMCreate.MediaHandlers;

namespace VMCreate.Tests.HyperV.VmCreation
{
    [TestClass]
    public sealed class VmDeploymentOrchestratorTests
    {
        private Mock<IVmPathService> _pathService;
        private Mock<IMediaHandlerFactory> _mediaHandlerFactory;
        private Mock<IHyperVManager> _hyperVManager;
        private Mock<ICloningIsoDownloader> _cloningIsoDownloader;
        private Mock<IVmCreationStrategy> _strategy;
        private Mock<ILogger<VmDeploymentOrchestrator>> _logger;
        private VmDeploymentOrchestrator _orchestrator;

        [TestInitialize]
        public void Setup()
        {
            _pathService = new Mock<IVmPathService>();
            _pathService.Setup(s => s.DefaultVmPath).Returns(@"C:\Hyper-V");
            _pathService.Setup(s => s.DefaultVhdxPath).Returns(@"C:\Hyper-V\Virtual hard disks");
            _pathService.Setup(s => s.GetVirtualHardDiskPath(It.IsAny<string>())).Returns((string vmName) =>
                $@"C:\Hyper-V\Virtual hard disks\{vmName}");

            _mediaHandlerFactory = new Mock<IMediaHandlerFactory>();
            _hyperVManager = new Mock<IHyperVManager>();
            _cloningIsoDownloader = new Mock<ICloningIsoDownloader>();
            _strategy = new Mock<IVmCreationStrategy>();
            _logger = new Mock<ILogger<VmDeploymentOrchestrator>>();

            _orchestrator = new VmDeploymentOrchestrator(
                _logger.Object,
                _mediaHandlerFactory.Object,
                _hyperVManager.Object,
                _cloningIsoDownloader.Object,
                new[] { _strategy.Object },
                _pathService.Object);
        }

        [TestMethod]
        public async Task DeployAsync_Success_SetsVmCreatedTrue()
        {
            // Arrange
            var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM" });
            var customizations = new VmCustomizations();
            var galleryItem = new GalleryItem { Name = "Test", DiskUri = "http://example.com/disk.iso", IsNativeHyperV = false };
            var mediaHandler = new FakeMediaHandler(DiskImageFormat.Iso);

            _mediaHandlerFactory.Setup(f => f.CreateHandler(It.IsAny<DiskImageFormat>())).Returns(mediaHandler);
            _strategy.Setup(s => s.CanHandle(galleryItem, DiskImageFormat.Iso)).Returns(true);
            _strategy.Setup(s => s.CreateVMAsync(It.IsAny<VmCreationContext>())).Returns(Task.CompletedTask);

            // Act
            VmDeploymentResult result = await _orchestrator.DeployAsync(
                plan, customizations, galleryItem, CancellationToken.None, null, "disk.iso");

            // Assert
            Assert.IsTrue(result.Success);
            _hyperVManager.Verify(h => h.GetVmHardDiskPathsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task DeployAsync_StrategyThrows_CleansUpVmIfCreated()
        {
            // Arrange
            var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM" });
            var customizations = new VmCustomizations();
            var galleryItem = new GalleryItem { Name = "Test", DiskUri = "http://example.com/disk.iso", IsNativeHyperV = false };
            var mediaHandler = new FakeMediaHandler(DiskImageFormat.Iso);

            _mediaHandlerFactory.Setup(f => f.CreateHandler(It.IsAny<DiskImageFormat>())).Returns(mediaHandler);
            _strategy.Setup(s => s.CanHandle(galleryItem, DiskImageFormat.Iso)).Returns(true);
            _strategy.Setup(s => s.CreateVMAsync(It.IsAny<VmCreationContext>())).ThrowsAsync(new InvalidOperationException("boom"));

            // Act
            VmDeploymentResult result = await _orchestrator.DeployAsync(
                plan, customizations, galleryItem, CancellationToken.None, null, "disk.iso");

            // Assert
            Assert.IsFalse(result.Success);
            Assert.AreEqual("boom", result.ErrorMessage);

            // vmCreated is false because the strategy threw, so we should not stop/remove the VM.
            _hyperVManager.Verify(h => h.StopVMAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _hyperVManager.Verify(h => h.RemoveVMAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task DeployAsync_NoStrategy_ReturnsFailureResult()
        {
            // Arrange
            var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM" });
            var customizations = new VmCustomizations();
            var galleryItem = new GalleryItem { Name = "Test", DiskUri = "http://example.com/disk.iso", IsNativeHyperV = false };
            var mediaHandler = new FakeMediaHandler(DiskImageFormat.Vmdk);

            _mediaHandlerFactory.Setup(f => f.CreateHandler(It.IsAny<DiskImageFormat>())).Returns(mediaHandler);
            _strategy.Setup(s => s.CanHandle(It.IsAny<GalleryItem>(), It.IsAny<DiskImageFormat>())).Returns(false);

            // Act
            VmDeploymentResult result = await _orchestrator.DeployAsync(
                plan, customizations, galleryItem, CancellationToken.None, null, "disk.vmdk");

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.ErrorMessage.Contains("No creation strategy found"));
        }
        private sealed class FakeMediaHandler : IMediaHandler
        {
            private readonly DiskImageFormat _fileType;
            private readonly MediaPreparationResult _result;

            public FakeMediaHandler(DiskImageFormat fileType, string finalPath = null)
            {
                _fileType = fileType;
                string ext = fileType.ToString().ToLowerInvariant();
                _result = new MediaPreparationResult(
                    finalPath ?? $@"C:\Hyper-V\Virtual hard disks\TestVM\disk.{ext}",
                    2);
            }

            public DiskImageFormat FileType => _fileType;

            public bool RequiresExtraction => false;
            public int VmGeneration => _result.VmGeneration;
            public long DetectedVirtualSizeBytes => _result.DetectedVirtualSizeBytes;

            public Task<MediaPreparationResult> PrepareMediaAsync(
                string sourceFile,
                string destinationPath,
                VmDeploymentPlan plan,
                GalleryItem galleryItem,
                IProgress<CreateVMProgressInfo> progress,
                CancellationToken cancellationToken)
                => Task.FromResult(_result);
        }
    }
}
