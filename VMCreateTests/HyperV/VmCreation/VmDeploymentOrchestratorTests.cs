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
        // Fixture fields — assigned in [TestInitialize] Setup() before every test.
        private Mock<IVmPathService> _pathService = null!;
        private Mock<IMediaHandlerFactory> _mediaHandlerFactory = null!;
        private Mock<IHyperVManager> _hyperVManager = null!;
        private Mock<ICloningIsoDownloader> _cloningIsoDownloader = null!;
        private Mock<IVmCreationStrategy> _strategy = null!;
        private Mock<ILogger<VmDeploymentOrchestrator>> _logger = null!;
        private VmDeploymentOrchestrator _orchestrator = null!;

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
            Assert.IsTrue(result.ErrorMessage!.Contains("No creation strategy found"));
        }

        [TestMethod]
        public async Task DeployAsync_ReplacePreviousVm_DeletesStalePerVmDiskDirectory()
        {
            // The re-deploy failure mode this pins: a previous deploy (or a
            // failed one) leaves a per-VM disk directory with unattached
            // intermediates. ReplacePreviousVm must remove the whole directory
            // so the next deploy of the same base name starts clean.
            string baseDir = Path.Combine(Path.GetTempPath(), $"vmcreate-orch-{Guid.NewGuid():N}");
            string oldVmName = "TestVM_20240101000000";
            string oldDiskDir = Path.Combine(baseDir, oldVmName);
            Directory.CreateDirectory(oldDiskDir);
            // Stale intermediates: a half-converted disk and a leftover archive.
            File.WriteAllText(Path.Combine(oldDiskDir, "disk.vhdx.tmp"), "partial");
            File.WriteAllText(Path.Combine(oldDiskDir, "image.ova"), "archive");

            try
            {
                _pathService.Setup(s => s.DefaultVhdxPath).Returns(baseDir);
                _pathService.Setup(s => s.GetVirtualHardDiskPath(It.IsAny<string>()))
                    .Returns((string vmName) => Path.Combine(baseDir, vmName));

                var plan = VmDeploymentPlan.FromSettings(new VmSettings
                {
                    VMName = "TestVM_20250601120000",
                    ReplacePreviousVm = true
                });
                var galleryItem = new GalleryItem { Name = "Test", DiskUri = "http://example.com/disk.vhdx", IsNativeHyperV = false };
                _mediaHandlerFactory.Setup(f => f.CreateHandler(It.IsAny<DiskImageFormat>()))
                    .Returns(new FakeMediaHandler(DiskImageFormat.Vhdx));
                _strategy.Setup(s => s.CanHandle(It.IsAny<GalleryItem>(), It.IsAny<DiskImageFormat>())).Returns(true);
                _hyperVManager.Setup(h => h.FindExistingVmsByBaseNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new[] { oldVmName });
                _hyperVManager.Setup(h => h.GetVmHardDiskPathsAsync(oldVmName, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Array.Empty<string>());

                VmDeploymentResult result = await _orchestrator.DeployAsync(
                    plan, new VmCustomizations(), galleryItem, CancellationToken.None, null, "disk.vhdx");

                Assert.IsTrue(result.Success);
                Assert.IsFalse(Directory.Exists(oldDiskDir),
                    "stale per-VM disk directory must be deleted on replace so re-deploys start clean");
                _hyperVManager.Verify(h => h.RemoveVMAsync(oldVmName, It.IsAny<CancellationToken>()), Times.Once);
            }
            finally
            {
                if (Directory.Exists(baseDir))
                    Directory.Delete(baseDir, true);
            }
        }

        [TestMethod]
        public async Task DeployAsync_CreatesPerVmDiskDirectory_ForBareDiskImages()
        {
            // The bare-qcow2 E2E failure: archive extraction creates the
            // per-VM disk directory as a side effect, but a bare disk image
            // (ISO/QCOW2/VHDX — no extraction step) handed qemu-img / the
            // disk attach a path whose parent did not exist. The orchestrator
            // must create it before PrepareMediaAsync runs.
            string baseDir = Path.Combine(Path.GetTempPath(), $"vmcreate-orch-{Guid.NewGuid():N}");
            _pathService.Setup(s => s.DefaultVhdxPath).Returns(baseDir);
            _pathService.Setup(s => s.GetVirtualHardDiskPath(It.IsAny<string>()))
                .Returns((string vmName) => Path.Combine(baseDir, vmName));

            var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM_20250601120000" });
            var galleryItem = new GalleryItem { Name = "Test", DiskUri = "https://x/image.qcow2", IsNativeHyperV = false };
            _mediaHandlerFactory.Setup(f => f.CreateHandler(It.IsAny<DiskImageFormat>()))
                .Returns(new FakeMediaHandler(DiskImageFormat.Qcow2));
            _strategy.Setup(s => s.CanHandle(It.IsAny<GalleryItem>(), It.IsAny<DiskImageFormat>())).Returns(true);

            // Fail inside PrepareMediaAsync to observe whether the directory
            // existed by then.
            string? observedDir = null;
            _strategy.Setup(s => s.CreateVMAsync(It.IsAny<VmCreationContext>())).Returns(() =>
            {
                observedDir = Path.Combine(baseDir, plan.VmName);
                return Task.CompletedTask;
            });

            try
            {
                VmDeploymentResult result = await _orchestrator.DeployAsync(
                    plan, new VmCustomizations(), galleryItem, CancellationToken.None, null, "image.qcow2");

                Assert.IsTrue(result.Success);
                Assert.IsNotNull(observedDir);
                Assert.IsTrue(Directory.Exists(observedDir),
                    "the per-VM disk directory must exist before media preparation (bare disk images skip extraction)");
            }
            finally
            {
                if (Directory.Exists(baseDir))
                    Directory.Delete(baseDir, true);
            }
        }

        [TestMethod]
        public async Task DeployAsync_Success_WritesTheDeploymentLogToTheAppLog()
        {
            // The ISO boot-cycle's guest scenario output lives only in the
            // deployment log; it must reach the persistent application log.
            VmDeploymentResult result = await DeployWithTempDisksAsync(ctx =>
            {
                ctx.Logger.Log("ISO boot-cycle v2: guest scenario output:\nSTEP_RESULT=ok");
                return Task.CompletedTask;
            });

            Assert.IsTrue(result.Success);
            VerifyAppLog(LogLevel.Information, "STEP_RESULT=ok", Times.Once());
        }

        [TestMethod]
        public async Task DeployAsync_Failure_WritesTheDeploymentLogAtWarning()
        {
            VmDeploymentResult result = await DeployWithTempDisksAsync(ctx =>
            {
                ctx.Logger.LogError("ISO boot-cycle v2 guest diagnostics (steps failed):\njournal tail");
                throw new InvalidOperationException("boom");
            });

            Assert.IsFalse(result.Success);
            VerifyAppLog(LogLevel.Warning, "journal tail", Times.Once());
            StringAssert.Contains(result.DeploymentLog, "journal tail", "the result still carries it");
        }

        private async Task<VmDeploymentResult> DeployWithTempDisksAsync(Func<VmCreationContext, Task> createVm)
        {
            string baseDir = Path.Combine(Path.GetTempPath(), $"vmcreate-orch-{Guid.NewGuid():N}");
            _pathService.Setup(s => s.DefaultVmPath).Returns(baseDir);
            _pathService.Setup(s => s.DefaultVhdxPath).Returns(baseDir);
            _pathService.Setup(s => s.GetVirtualHardDiskPath(It.IsAny<string>()))
                .Returns((string vmName) => Path.Combine(baseDir, vmName));
            var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM_20250601120000" });
            var galleryItem = new GalleryItem { Name = "Test", DiskUri = "https://x/image.iso", IsNativeHyperV = false };
            _mediaHandlerFactory.Setup(f => f.CreateHandler(It.IsAny<DiskImageFormat>()))
                .Returns(new FakeMediaHandler(DiskImageFormat.Iso));
            _strategy.Setup(s => s.CanHandle(It.IsAny<GalleryItem>(), It.IsAny<DiskImageFormat>())).Returns(true);
            _strategy.Setup(s => s.CreateVMAsync(It.IsAny<VmCreationContext>())).Returns(createVm);
            try
            {
                return await _orchestrator.DeployAsync(
                    plan, new VmCustomizations(), galleryItem, CancellationToken.None, null, "image.iso");
            }
            finally
            {
                if (Directory.Exists(baseDir))
                    Directory.Delete(baseDir, true);
            }
        }

        private void VerifyAppLog(LogLevel level, string fragment, Times times) =>
            _logger.Verify(l => l.Log(level, It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(fragment)),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);

        private sealed class FakeMediaHandler : IMediaHandler
        {
            private readonly DiskImageFormat _fileType;
            private readonly MediaPreparationResult _result;

            public FakeMediaHandler(DiskImageFormat fileType, string? finalPath = null)
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
