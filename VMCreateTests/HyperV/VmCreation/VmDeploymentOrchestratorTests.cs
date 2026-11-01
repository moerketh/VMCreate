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

        [TestMethod]
        [DoNotParallelize] // flips the process-wide Debug-preservation switch
        public async Task DeployAsync_StrategyThrowsAfterNewVm_RemovesTheVmAndItsDisks()
        {
            // The stranded-VHDX bug this pins: cleanup used to key off a flag
            // set only after the strategy RETURNED, so a strategy that threw
            // after New-VM left the VM registered and its disks on the drive.
            string baseDir = NewTempDir();
            string vmName = "TestVM_20250601120000";
            string diskDir = Directory.CreateDirectory(Path.Combine(baseDir, vmName)).FullName;
            string disk = Touch(Path.Combine(diskDir, vmName + ".vhdx"));
            string intermediate = Touch(Path.Combine(diskDir, "Rocky-9-Azure-Base.latest.x86_64.vhdfixed"));
            string seed = Touch(Path.Combine(baseDir, vmName + "_cidata.iso"));
            // Debug builds (this test run) preserve failed VMs by design.
            DebugPreservation.TestOverride = false;
            try
            {
                UseTempPaths(baseDir);
                _hyperVManager.SetupSequence(h => h.VmExistsAsync(vmName, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(false)   // pre-flight: the name is free
                    .ReturnsAsync(true);   // cleanup: the strategy got as far as New-VM
                _hyperVManager.Setup(h => h.GetVmDiskChainPathsAsync(vmName, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new[] { disk });
                SetupStrategy(DiskImageFormat.Vhdx, _ => throw new InvalidOperationException("boom"));

                VmDeploymentResult result = await DeployAsync(vmName, "disk.vhdx");

                Assert.IsFalse(result.Success);
                _hyperVManager.Verify(h => h.RemoveVMAsync(vmName, It.IsAny<CancellationToken>()), Times.Once);
                Assert.IsFalse(File.Exists(disk), "the VM's disk must be deleted");
                Assert.IsFalse(File.Exists(intermediate), "intermediates in the per-VM directory must be deleted");
                Assert.IsFalse(Directory.Exists(diskDir), "the per-VM disk directory must be deleted");
                Assert.IsFalse(File.Exists(seed), "a seed ISO outside the per-VM directory must be deleted");
            }
            finally
            {
                DebugPreservation.TestOverride = null;
                DeleteTempDir(baseDir);
            }
        }

        [TestMethod]
        public async Task DeployAsync_VmNameAlreadyExists_FailsWithoutTouchingThatVm()
        {
            string baseDir = NewTempDir();
            string vmName = "TestVM_20250601120000";
            string diskDir = Directory.CreateDirectory(Path.Combine(baseDir, vmName)).FullName;
            string disk = Touch(Path.Combine(diskDir, vmName + ".vhdx"));
            try
            {
                UseTempPaths(baseDir);
                _hyperVManager.Setup(h => h.VmExistsAsync(vmName, It.IsAny<CancellationToken>())).ReturnsAsync(true);
                SetupStrategy(DiskImageFormat.Vhdx, _ => Task.CompletedTask);

                VmDeploymentResult result = await DeployAsync(vmName, "disk.vhdx");

                Assert.IsFalse(result.Success);
                StringAssert.Contains(result.ErrorMessage!, "already exists");
                _strategy.Verify(s => s.CreateVMAsync(It.IsAny<VmCreationContext>()), Times.Never);
                _hyperVManager.Verify(h => h.StopVMAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
                _hyperVManager.Verify(h => h.RemoveVMAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
                Assert.IsTrue(File.Exists(disk), "another VM's disk must survive");
            }
            finally { DeleteTempDir(baseDir); }
        }

        [TestMethod]
        public async Task DeployAsync_Success_PrunesUnattachedIntermediatesAndKeepsAttachedMedia()
        {
            // The leftover-source bug: an extracted disk that had to be
            // converted (Rocky's .vhdfixed, Parrot's .qcow2) stayed next to
            // the converted VHDX after every deployment.
            string baseDir = NewTempDir();
            string vmName = "TestVM_20250601120000";
            string diskDir = Directory.CreateDirectory(Path.Combine(baseDir, vmName)).FullName;
            string disk = Touch(Path.Combine(diskDir, vmName + ".vhdx"));
            string dvd = Touch(Path.Combine(diskDir, "installer.iso"));
            string source = Touch(Path.Combine(diskDir, "Parrot-security-7.2_amd64.qcow2"));
            string nestedDir = Directory.CreateDirectory(Path.Combine(diskDir, "ova")).FullName;
            string sideFile = Touch(Path.Combine(nestedDir, "image.ovf"));
            try
            {
                UseTempPaths(baseDir);
                _hyperVManager.Setup(h => h.GetVmDiskChainPathsAsync(vmName, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new[] { disk });
                _hyperVManager.Setup(h => h.GetVmDvdMediaPathsAsync(vmName, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new[] { dvd });
                SetupStrategy(DiskImageFormat.Vhdx, _ => Task.CompletedTask);

                VmDeploymentResult result = await DeployAsync(vmName, "disk.vhdx");

                Assert.IsTrue(result.Success);
                Assert.IsTrue(File.Exists(disk), "the attached disk must be kept");
                Assert.IsTrue(File.Exists(dvd), "attached DVD media must be kept");
                Assert.IsFalse(File.Exists(source), "the converted-from source must be deleted");
                Assert.IsFalse(File.Exists(sideFile), "archive side files must be deleted");
                Assert.IsFalse(Directory.Exists(nestedDir), "emptied subdirectories must be removed");
            }
            finally { DeleteTempDir(baseDir); }
        }

        [TestMethod]
        public async Task DeployAsync_Success_DoesNotPruneWhenHyperVReportsNoMedia()
        {
            // An empty media answer means the query failed; pruning on it
            // would delete the VM's own disk.
            string baseDir = NewTempDir();
            string vmName = "TestVM_20250601120000";
            string diskDir = Directory.CreateDirectory(Path.Combine(baseDir, vmName)).FullName;
            string disk = Touch(Path.Combine(diskDir, vmName + ".vhdx"));
            try
            {
                UseTempPaths(baseDir);
                SetupStrategy(DiskImageFormat.Vhdx, _ => Task.CompletedTask);

                VmDeploymentResult result = await DeployAsync(vmName, "disk.vhdx");

                Assert.IsTrue(result.Success);
                Assert.IsTrue(File.Exists(disk));
            }
            finally { DeleteTempDir(baseDir); }
        }

        [TestMethod]
        [DataRow(true, true, true, DisplayName = "enabled + guest ready → checkpoint")]
        [DataRow(true, false, false, DisplayName = "guest not ready (manual install) → none")]
        [DataRow(false, true, false, DisplayName = "disabled (--no-checkpoint) → none")]
        public async Task DeployAsync_CleanCheckpoint_OnlyWhenEnabledAndGuestReady(bool enabled, bool guestReady, bool expected)
        {
            string baseDir = NewTempDir();
            try
            {
                UseTempPaths(baseDir);
                SetupStrategy(DiskImageFormat.Vhdx, ctx =>
                {
                    ctx.GuestReady = guestReady;
                    return Task.CompletedTask;
                });

                VmDeploymentResult result = await DeployAsync("TestVM_20250601120000", "disk.vhdx",
                    createCleanCheckpoint: enabled);

                Assert.IsTrue(result.Success);
                _hyperVManager.Verify(h => h.CreateCheckpointAsync(
                        "TestVM_20250601120000", VmDeploymentOrchestrator.CleanCheckpointName, It.IsAny<CancellationToken>()),
                    expected ? Times.Once() : Times.Never());
            }
            finally { DeleteTempDir(baseDir); }
        }

        [TestMethod]
        public async Task DeployAsync_CheckpointFailure_KeepsTheDeployment()
        {
            string baseDir = NewTempDir();
            try
            {
                UseTempPaths(baseDir);
                _hyperVManager.Setup(h => h.CreateCheckpointAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new Exception("checkpoint failed"));
                SetupStrategy(DiskImageFormat.Vhdx, ctx =>
                {
                    ctx.GuestReady = true;
                    return Task.CompletedTask;
                });

                VmDeploymentResult result = await DeployAsync("TestVM_20250601120000", "disk.vhdx");

                Assert.IsTrue(result.Success, "a failed checkpoint must not fail the deployment");
                _hyperVManager.Verify(h => h.RemoveVMAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
                StringAssert.Contains(result.DeploymentLog, "Could not create checkpoint");
            }
            finally { DeleteTempDir(baseDir); }
        }

        [TestMethod]
        public async Task DeployAsync_ReplacePreviousVm_DeletesTheWholeCheckpointChain()
        {
            // With a checkpoint the attached disk is the .avhdx; the parent
            // .vhdx underneath used to be stranded on replace.
            string baseDir = NewTempDir();
            string oldVmName = "TestVM_20240101000000";
            string oldDir = Directory.CreateDirectory(Path.Combine(baseDir, oldVmName)).FullName;
            string parent = Touch(Path.Combine(oldDir, oldVmName + ".vhdx"));
            string child = Touch(Path.Combine(oldDir, oldVmName + "_8818447A.avhdx"));
            try
            {
                UseTempPaths(baseDir);
                _hyperVManager.Setup(h => h.FindExistingVmsByBaseNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new[] { oldVmName });
                _hyperVManager.Setup(h => h.GetVmDiskChainPathsAsync(oldVmName, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new[] { child, parent });
                SetupStrategy(DiskImageFormat.Vhdx, _ => Task.CompletedTask);

                VmDeploymentResult result = await DeployAsync("TestVM_20250601120000", "disk.vhdx", replace: true);

                Assert.IsTrue(result.Success);
                Assert.IsFalse(File.Exists(child));
                Assert.IsFalse(File.Exists(parent), "the checkpoint's parent disk must not be stranded");
            }
            finally { DeleteTempDir(baseDir); }
        }

        private static string NewTempDir() =>
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"vmcreate-orch-{Guid.NewGuid():N}")).FullName;

        private static void DeleteTempDir(string dir)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }

        private static string Touch(string path)
        {
            File.WriteAllText(path, "x");
            return path;
        }

        private void UseTempPaths(string baseDir)
        {
            _pathService.Setup(s => s.DefaultVmPath).Returns(baseDir);
            _pathService.Setup(s => s.DefaultVhdxPath).Returns(baseDir);
            _pathService.Setup(s => s.GetVirtualHardDiskPath(It.IsAny<string>()))
                .Returns((string vmName) => Path.Combine(baseDir, vmName));
        }

        private void SetupStrategy(DiskImageFormat format, Func<VmCreationContext, Task> createVm)
        {
            _mediaHandlerFactory.Setup(f => f.CreateHandler(It.IsAny<DiskImageFormat>()))
                .Returns(new FakeMediaHandler(format));
            _strategy.Setup(s => s.CanHandle(It.IsAny<GalleryItem>(), It.IsAny<DiskImageFormat>())).Returns(true);
            _strategy.Setup(s => s.CreateVMAsync(It.IsAny<VmCreationContext>())).Returns(createVm);
        }

        [TestMethod]
        public async Task DeployAsync_Success_RecordsManagerMetadata()
        {
            var recorder = UseMetadataRecorder();
            string baseDir = NewTempDir();
            try
            {
                UseTempPaths(baseDir);
                SetupStrategy(DiskImageFormat.Vhdx, _ => Task.CompletedTask);

                VmDeploymentResult result = await DeployAsync("TestVM_20250601120000", "disk.vhdx");

                Assert.IsTrue(result.Success);
                recorder.Verify(r => r.RecordDeploymentAsync(
                        It.Is<VmDeploymentPlan>(p => p.VmName == "TestVM_20250601120000"),
                        It.Is<GalleryItem>(g => g.Name == "Test"),
                        It.IsAny<CancellationToken>()),
                    Times.Once);
            }
            finally { DeleteTempDir(baseDir); }
        }

        [TestMethod]
        public async Task DeployAsync_MetadataFailure_KeepsTheDeployment()
        {
            var recorder = UseMetadataRecorder();
            recorder.Setup(r => r.RecordDeploymentAsync(It.IsAny<VmDeploymentPlan>(), It.IsAny<GalleryItem>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("store locked"));
            string baseDir = NewTempDir();
            try
            {
                UseTempPaths(baseDir);
                SetupStrategy(DiskImageFormat.Vhdx, _ => Task.CompletedTask);

                VmDeploymentResult result = await DeployAsync("TestVM_20250601120000", "disk.vhdx");

                Assert.IsTrue(result.Success, "bookkeeping must not fail (and delete) a finished VM");
                _hyperVManager.Verify(h => h.RemoveVMAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
                StringAssert.Contains(result.DeploymentLog, "Could not record folder/tags/provenance");
            }
            finally { DeleteTempDir(baseDir); }
        }

        [TestMethod]
        public async Task DeployAsync_StrategyThrows_RecordsNoMetadata()
        {
            var recorder = UseMetadataRecorder();
            string baseDir = NewTempDir();
            try
            {
                UseTempPaths(baseDir);
                SetupStrategy(DiskImageFormat.Vhdx, _ => throw new InvalidOperationException("boom"));

                VmDeploymentResult result = await DeployAsync("TestVM_20250601120000", "disk.vhdx");

                Assert.IsFalse(result.Success);
                recorder.Verify(r => r.RecordDeploymentAsync(It.IsAny<VmDeploymentPlan>(), It.IsAny<GalleryItem>(), It.IsAny<CancellationToken>()), Times.Never);
            }
            finally { DeleteTempDir(baseDir); }
        }

        /// <summary>Rebuilds the orchestrator with a metadata recorder (the default fixture has none).</summary>
        private Mock<IVmMetadataRecorder> UseMetadataRecorder()
        {
            var recorder = new Mock<IVmMetadataRecorder>();
            _orchestrator = new VmDeploymentOrchestrator(
                _logger.Object,
                _mediaHandlerFactory.Object,
                _hyperVManager.Object,
                _cloningIsoDownloader.Object,
                new[] { _strategy.Object },
                _pathService.Object,
                recorder.Object);
            return recorder;
        }

        private Task<VmDeploymentResult> DeployAsync(string vmName, string sourceFile,
            bool createCleanCheckpoint = true, bool replace = false)
        {
            var plan = VmDeploymentPlan.FromSettings(new VmSettings
            {
                VMName = vmName,
                CreateCleanCheckpoint = createCleanCheckpoint,
                ReplacePreviousVm = replace,
            });
            var galleryItem = new GalleryItem { Name = "Test", DiskUri = "https://x/" + sourceFile, IsNativeHyperV = false };
            return _orchestrator.DeployAsync(plan, new VmCustomizations(), galleryItem, CancellationToken.None, null, sourceFile);
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
