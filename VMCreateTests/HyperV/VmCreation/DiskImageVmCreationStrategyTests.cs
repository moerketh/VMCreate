using CreateVM.HyperV.vmbus;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
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
        // Fixture fields — assigned in [TestInitialize] Setup() before every test.
        private Mock<IVmLifecycleManager> _lifecycleManager = null!;
        private Mock<IVmDiskManager> _diskManager = null!;
        private Mock<IVmBootManager> _bootManager = null!;
        private Mock<IVmNetworkManager> _networkManager = null!;
        private Mock<IVmConfigManager> _configManager = null!;
        private Mock<IGuestShellFactory> _guestShellFactory = null!;
        private Mock<ISshKeyManager> _sshKeyManager = null!;
        private Mock<IKvpHeartbeatGate> _kvpHeartbeatGate = null!;
        private Mock<IGuestDiagnosticsCollector> _diagnosticsCollector = null!;
        private Mock<IPostBootCustomizationService> _postBootService = null!;
        private Mock<IPostBootStepPlanner> _stepPlanner = null!;
        private Mock<IHostNetworkService> _hostNetworkService = null!;
        private Mock<IIsoBootCycleRunner> _isoBootRunner = null!;
        private Mock<ICloningIsoDownloader> _cloningIsoDownloader = null!;
        private Mock<IGuestShell> _shell = null!;
        private DiskImageVmCreationStrategy _strategy = null!;
        private VmSettings _vmSettings = null!;
        private VmDeploymentPlan _plan = null!;
        private VmCustomizations _customizations = null!;
        private GalleryItem _item = null!;
        private string _mediaPath = null!;
        private string _cloningIsoPath = null!;

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
            _kvpHeartbeatGate = new Mock<IKvpHeartbeatGate>();
            _diagnosticsCollector = new Mock<IGuestDiagnosticsCollector>();
            _postBootService = new Mock<IPostBootCustomizationService>();
            _stepPlanner = new Mock<IPostBootStepPlanner>();
            // Planner defaults mirror the old inline predicate so existing
            // strategy tests keep their Gen-2 ISO-boot expectations; tests
            // that need a different verdict override this setup.
            _stepPlanner
                .Setup(p => p.NeedsIsoBootCycle(It.IsAny<GalleryItem?>(), It.IsAny<VmCustomizations?>()))
                .Returns<GalleryItem?, VmCustomizations?>((item, c) =>
                    c?.ConfigureXrdp == true
                    || _postBootService.Object.HasLinuxPostBootSteps(item!, c!));
            _hostNetworkService = new Mock<IHostNetworkService>();
            _isoBootRunner = new Mock<IIsoBootCycleRunner>();
            _cloningIsoDownloader = new Mock<ICloningIsoDownloader>();
            _cloningIsoDownloader.Setup(d => d.VerifyCachedIsoGuardedAsync(
                It.IsAny<VmDeploymentPlan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<IIsoReadGuard>());

            _vmSettings = new VmSettings
            {
                // Unique per test run: the seed ISO and VM paths derive from
                // the VM name, and the assembly runs test methods in
                // parallel — a shared "TestVM" means every cloud-init test
                // builds and reads the SAME seed file concurrently.
                VMName = "TestVM" + Guid.NewGuid().ToString("N")[..8],
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

            // Cloud-init seed builds need a real public key line.
            _sshKeyManager.Setup(s => s.EnsureKeyPairAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync("ssh-ed25519 AAAAC3Nzatest vmcreate@test");

            // Shared guest shell mock: tests can setup RunCommandAsync for
            // witness probes (KVP gate timeout) and shell-dependent steps.
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.WaitForReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _guestShellFactory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<string>())).Returns(_shell.Object);
            _guestShellFactory.Setup(f => f.CreateWithPinnedIp(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(_shell.Object);
            _sshKeyManager.Setup(s => s.GetPrivateKeyPath(It.IsAny<string>())).Returns("C:\\Users\\Test\\.ssh\\id_rsa");

            // KVP heartbeat gate: pass-through default (guest daemon alive).
            // Loose mocks also produce a null LastDiagnostics — the strategy
            // must tolerate that.
            _kvpHeartbeatGate.Setup(g => g.WaitForGuestKvpAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(true);

            _vhdxPathServiceRoot = Path.Combine(
                Path.GetTempPath(), "vmcreate-strategy-tests-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_vhdxPathServiceRoot);
            _strategy = new DiskImageVmCreationStrategy(
                _lifecycleManager.Object,
                _diskManager.Object,
                _bootManager.Object,
                _networkManager.Object,
                _configManager.Object,
                _guestShellFactory.Object,
                _sshKeyManager.Object,
                _kvpHeartbeatGate.Object,
                _diagnosticsCollector.Object,
                _postBootService.Object,
                _stepPlanner.Object,
                _hostNetworkService.Object,
                new Mock<ILogger<DiskImageVmCreationStrategy>>().Object,
                // A temp root, NOT the real Hyper-V folders: the strategy
                // builds and deletes real seed ISOs under DefaultVhdxPath, and
                // test artifacts must not land in the machine's actual VM
                // disk directory.
                Mock.Of<IVmPathService>(
                    s => s.DefaultVmPath == _vhdxPathServiceRoot
                         && s.DefaultVhdxPath == _vhdxPathServiceRoot
                         && s.GetVirtualHardDiskPath(It.IsAny<string>())
                             == Path.Combine(_vhdxPathServiceRoot, "disks")),
                _isoBootRunner.Object,
                _cloningIsoDownloader.Object);
        }

        private string _vhdxPathServiceRoot = null!;

        [TestCleanup]
        public void Cleanup()
        {
            try { File.Delete(_mediaPath); } catch { }
            try { File.Delete(_cloningIsoPath); } catch { }
            try { File.Delete(_customUserDataPath); } catch { }
            // The temp path-service root holds the built seed ISOs (and the
            // MBR-clone working files); delete the whole tree so repeated
            // runs never accumulate artifacts.
            try { if (_vhdxPathServiceRoot != null) Directory.Delete(_vhdxPathServiceRoot, true); } catch { }
        }

        [TestMethod]
        [DataRow(1, DisplayName = "Gen 1: ISO boot cycle, two starts")]
        [DataRow(2, DisplayName = "Gen 2")]
        public async Task CreateVMAsync_SetsEnhancedSessionTransportBeforeFirstStart(int generation)
        {
            // Hyper-V only polls the guest's vsock:3389 listener on a worker
            // STARTED with the HvSocket transport; setting it on a running VM
            // leaves Enhanced Session unavailable until a host restart.
            var calls = new List<string>();
            _configManager.Setup(c => c.SetEnhancedSession(_plan, It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("SetEnhancedSession")).Returns(Task.CompletedTask);
            _lifecycleManager.Setup(l => l.StartVM(_plan, It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("StartVM")).Returns(Task.CompletedTask);

            await ExecuteAsync(generation);

            CollectionAssert.Contains(calls, "SetEnhancedSession");
            Assert.AreEqual("SetEnhancedSession", calls[0],
                $"the transport must be set before the first StartVM (calls: {string.Join(", ", calls)})");
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

            _lifecycleManager.Verify(h => h.CreateVMAsync(_plan, _vhdxPathServiceRoot, 2, It.IsAny<CancellationToken>()), Times.Once);
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
            _customizations.InstallOpenVpn = false;
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(false);

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
        public async Task CreateVMAsync_Gen2_AttachesBootDvd_WhenOnlyDefaultPostBootStepsApply()
        {
            _customizations.ConfigureXrdp = false;
            _customizations.InstallOpenVpn = true;
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(true);

            await ExecuteAsync(generation: 2);

            _bootManager.Verify(h => h.AddBootDvd(_plan, _cloningIsoPath, It.IsAny<CancellationToken>()), Times.Once);
            _isoBootRunner.Verify(r => r.RunAsync(
                It.IsAny<VmCreationContext>(), 2, _mediaPath, _customizations,
                It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_Gen1_AddsNewAndExistingHardDrives()
        {
            await ExecuteAsync(generation: 1);

            _diskManager.Verify(h => h.AddNewHardDrive(_plan, _vhdxPathServiceRoot, It.IsAny<CancellationToken>()), Times.Once);
            _diskManager.Verify(h => h.AddExistingHardDrive(_plan, _mediaPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.SetFirstBootToDvd(_plan, It.IsAny<CancellationToken>()), Times.Once);
            _isoBootRunner.Verify(r => r.RunAsync(
                It.IsAny<VmCreationContext>(), 1, _mediaPath, _customizations,
                It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        // ── Cloud-init (Elastic SIEM) path + stock-flow regression pins ────────

        private static GalleryItem CloudInitItem() => new()
        {
            Name = "Elastic SIEM",
            Publisher = "t",
            Description = "t",
            DiskUri = "https://x/Rocky-10-Azure-Base.latest.x86_64.vhdfixed.xz",
            SecureBoot = "false",
            EnhancedSessionTransportType = "HvSocket",
            Version = "Rocky 10",
            Tags = new List<string> { "cloud-init", "elastic-siem", "headless" },
            LinuxDistro = LinuxDistro.Rocky,
        };

        [TestMethod]
        public async Task CreateVMAsync_CloudInitGen2_BootsDiskWithSeed_NoCloningIso()
        {
            // The appliance path: UEFI-bootable cloud image attaches the
            // converted disk + cidata seed and boots the DISK — the cloning
            // ISO is never attached and the clone cycle never runs.
            _item = CloudInitItem();
            _customizations = new VmCustomizations();

            await ExecuteAsync(generation: 2);

            _diskManager.Verify(h => h.AddExistingHardDrive(_plan, _mediaPath, It.IsAny<CancellationToken>()), Times.Once);
            _diskManager.Verify(h => h.AddNewHardDrive(_plan, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _bootManager.Verify(h => h.AddBootDvd(_plan, It.Is<string>(p => p.EndsWith("hyperv-convert.iso", StringComparison.OrdinalIgnoreCase) || p == _cloningIsoPath), It.IsAny<CancellationToken>()), Times.Never,
                "cloud-init Gen 2 must never attach the cloning ISO");
            _bootManager.Verify(h => h.AddBootDvd(_plan, It.Is<string>(p => p.EndsWith("_cidata.iso", StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()), Times.Once,
                "the NoCloud seed must be attached");
            _bootManager.Verify(h => h.SetFirstBootToHardDrive(_plan, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.SetFirstBootToDvd(_plan, It.IsAny<CancellationToken>()), Times.Never);
            _isoBootRunner.Verify(r => r.RunAsync(
                It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<VmCustomizations>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Never,
                "cloud-init Gen 2 must never run the clone cycle");
        }

        [TestMethod]
        public async Task CreateVMAsync_CloudInitGen2_RunsPostBootOverSsh_AndDetachesSeed()
        {
            _item = CloudInitItem();
            _customizations = new VmCustomizations();
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(true);

            await ExecuteAsync(generation: 2);

            // Post-boot ran (seed path), no temp-NIC dance (that belongs to the
            // clone cycle), and the seed was detached afterwards. Two calls:
            // the split around the KVP gate (main phase + SSH-restore tail).
            _postBootService.Verify(p => p.RunLinuxPostBootAsync(
                It.IsAny<IGuestShell>(), _plan, _item, _customizations,
                It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ISet<string>?>()), Times.Exactly(2));
            _networkManager.Verify(n => n.AddTemporaryNetworkAdapter(_plan, It.IsAny<CancellationToken>()), Times.Never);
            _bootManager.Verify(h => h.RemoveBootDvd(_plan, It.Is<string>(p => p.EndsWith("_cidata.iso", StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_StockGen2WithPostBoot_StillUsesCloningIso()
        {
            // Regression pin: a NON-cloud-init image with post-boot steps must
            // keep the stock cloning-ISO flow exactly as before the SIEM work.
            _customizations.ConfigureXrdp = false;
            _customizations.InstallOpenVpn = true;
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(true);

            await ExecuteAsync(generation: 2);

            _bootManager.Verify(h => h.AddBootDvd(_plan, _cloningIsoPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.SetFirstBootToDvd(_plan, It.IsAny<CancellationToken>()), Times.Once);
            _isoBootRunner.Verify(r => r.RunAsync(
                It.IsAny<VmCreationContext>(), 2, _mediaPath, _customizations,
                It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.AddBootDvd(_plan, It.Is<string>(p => p.EndsWith("_cidata.iso", StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()), Times.Never,
                "non-cloud-init images never get a seed");
        }

        // ── Static-IP deploy path (--static-ip) ────────────────────────────────

        [TestMethod]
        public async Task CreateVMAsync_StaticIp_PostBootShellTargetsThePinnedAddress()
        {
            // The subtle requirement: a statically configured guest may never
            // appear in Hyper-V's DHCP-lease-driven IP discovery, so the
            // post-boot shell must be built on the PINNED address (host part
            // of the CIDR), not on the discovery shell. Cloud-init item: the
            // only disk-image flow whose seed carries the network config.
            _item = CloudInitItem();
            _customizations = new VmCustomizations
            {
                StaticIp = "192.168.10.50/24",
                StaticGateway = "192.168.10.1",
                InstallOpenVpn = true,
            };
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(true);

            await ExecuteAsync(generation: 2);

            _guestShellFactory.Verify(f => f.CreateWithPinnedIp(
                _plan.VmName, It.IsAny<string>(), It.IsAny<string>(), "192.168.10.50"), Times.Once,
                "the static address (host part of the CIDR) is the SSH target");
            _guestShellFactory.Verify(f => f.Create(_plan.VmName, It.IsAny<string>()), Times.Never,
                "discovery mode must not be used for a statically configured guest");
        }

        [TestMethod]
        public async Task CreateVMAsync_StaticIp_OnCloningIsoImage_FailsLoudly()
        {
            // A non-cloud-init disk image is customized via the cloning ISO,
            // which carries no seed: the static address would never reach the
            // guest while the pinned-SSH wait burns its budget. The deploy
            // must fail before the VM skeleton exists.
            _customizations = new VmCustomizations
            {
                StaticIp = "192.168.10.50/24",
                InstallOpenVpn = true,
            };
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(true);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(generation: 2));

            StringAssert.Contains(ex.Message, "--static-ip");
            _lifecycleManager.Verify(m => m.CreateVMAsync(
                It.IsAny<VmDeploymentPlan>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()), Times.Never,
                "the unsupported combination must fail before any VM work");
        }

        [TestMethod]
        public async Task CreateVMAsync_StaticIp_UnderDiscoveryModeUnchanged()
        {
            // Regression pin: without --static-ip the post-boot shell keeps
            // the discovery mode exactly as before the feature.
            _customizations = new VmCustomizations { InstallOpenVpn = true };
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(true);

            await ExecuteAsync(generation: 2);

            _guestShellFactory.Verify(f => f.Create(_plan.VmName, It.IsAny<string>()), Times.Once);
            _guestShellFactory.Verify(f => f.CreateWithPinnedIp(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateVMAsync_StaticIp_SeedCarriesTheNetworkConfig()
        {
            // The cloud-init seed must carry the static addressing (rendered
            // network-config file) — validated here by the seed-build call
            // happening at all; byte-level content is pinned in
            // StaticNetworkSeedTests.
            _item = CloudInitItem();
            _customizations = new VmCustomizations
            {
                StaticIp = "192.168.10.50/24",
                InstallOpenVpn = true,
            };
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(true);

            await ExecuteAsync(generation: 2);

            _bootManager.Verify(h => h.AddBootDvd(_plan, It.Is<string>(p => p.EndsWith("_cidata.iso", StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()), Times.Once,
                "the seed with the network config must be attached");
        }

        [TestMethod]
        public async Task CreateVMAsync_CloudInitGen1_StillClonesLayoutWithSeed()
        {
            // A hypothetical cloud-init image WITHOUT an ESP (Ubuntu-class):
            // the clone cycle must still convert the layout, with the seed
            // attached alongside for the post-clone first boot.
            _item = CloudInitItem();
            _customizations = new VmCustomizations();

            await ExecuteAsync(generation: 1);

            _diskManager.Verify(h => h.AddNewHardDrive(_plan, _vhdxPathServiceRoot, It.IsAny<CancellationToken>()), Times.Once);
            _diskManager.Verify(h => h.AddExistingHardDrive(_plan, _mediaPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.AddBootDvd(_plan, _cloningIsoPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.AddBootDvd(_plan, It.Is<string>(p => p.EndsWith("_cidata.iso", StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.SetFirstBootToDvd(_plan, It.IsAny<CancellationToken>()), Times.Once);
            _isoBootRunner.Verify(r => r.RunAsync(
                It.IsAny<VmCreationContext>(), 1, _mediaPath, _customizations,
                It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        // ── --cloud-init on untagged images (the raw-cloud-image path) ──────

        // Per-test file: the assembly parallelizes test methods, so tests
        // cannot share one user-data path (cleanup would delete it while
        // another test's seed build is still reading it).
        private string _customUserDataPath = null!;

        private void WriteCustomUserData()
        {
            _customUserDataPath = Path.Combine(
                Path.GetTempPath(), "vmcreate-tests-userdata-" + Guid.NewGuid().ToString("N") + ".yaml");
            File.WriteAllText(_customUserDataPath, "#cloud-config\npackages: [htop]\n");
        }

        [TestMethod]
        public async Task CreateVMAsync_UntaggedGen2WithCloudInitFile_BootsDiskWithSeed_NoCloningIso()
        {
            // The headline --cloud-init case: a raw cloud image with NO
            // gallery tag. The user-data file alone must imply the seed
            // path — attach the NoCloud seed, boot the disk, never attach
            // the cloning ISO, never run the clone cycle. This one
            // condition is what makes any cloud-init-enabled image a
            // one-command unattended deploy.
            WriteCustomUserData();
            _customizations = new VmCustomizations { CloudInitUserDataPath = _customUserDataPath };
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(false);

            await ExecuteAsync(generation: 2);

            _diskManager.Verify(h => h.AddExistingHardDrive(_plan, _mediaPath, It.IsAny<CancellationToken>()), Times.Once);
            _diskManager.Verify(h => h.AddNewHardDrive(_plan, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _bootManager.Verify(h => h.AddBootDvd(_plan, _cloningIsoPath, It.IsAny<CancellationToken>()), Times.Never,
                "a --cloud-init deploy must never attach the cloning ISO on the Gen-2 path");
            _bootManager.Verify(h => h.AddBootDvd(_plan, It.Is<string>(p => p.EndsWith("_cidata.iso", StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()), Times.Once,
                "the NoCloud seed must be attached");
            _bootManager.Verify(h => h.SetFirstBootToHardDrive(_plan, It.IsAny<CancellationToken>()), Times.Once);
            _isoBootRunner.Verify(r => r.RunAsync(
                It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<VmCustomizations>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Never,
                "a --cloud-init deploy must never run the clone cycle on the Gen-2 path");
        }

        [TestMethod]
        public async Task CreateVMAsync_UntaggedGen1WithCloudInitFile_ClonesLayoutWithSeed()
        {
            // MBR/BIOS-classified source: the --cloud-init deploy still goes
            // through the clone cycle (layout conversion is mandatory) with
            // the seed attached as the second DVD for the post-clone first
            // boot — the same shape as a tagged cloud-init Gen 1 deploy.
            WriteCustomUserData();
            _customizations = new VmCustomizations { CloudInitUserDataPath = _customUserDataPath };
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(false);

            await ExecuteAsync(generation: 1);

            _diskManager.Verify(h => h.AddNewHardDrive(_plan, _vhdxPathServiceRoot, It.IsAny<CancellationToken>()), Times.Once);
            _diskManager.Verify(h => h.AddExistingHardDrive(_plan, _mediaPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.AddBootDvd(_plan, _cloningIsoPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.AddBootDvd(_plan, It.Is<string>(p => p.EndsWith("_cidata.iso", StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()), Times.Once,
                "the seed must ride along on the clone path too");
            _bootManager.Verify(h => h.SetFirstBootToDvd(_plan, It.IsAny<CancellationToken>()), Times.Once);
            _isoBootRunner.Verify(r => r.RunAsync(
                It.IsAny<VmCreationContext>(), 1, _mediaPath, It.IsAny<VmCustomizations>(),
                It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_UntaggedGen2WithCloudInitFile_EmbedsCustomUserDataInSeed()
        {
            // The seed built for a --cloud-init deploy must carry the user's
            // content merged ahead of the built-in block (multipart), not
            // just the stock cloud-config.
            WriteCustomUserData();
            _customizations = new VmCustomizations { CloudInitUserDataPath = _customUserDataPath };
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(false);
            string? builtSeed = null;
            _bootManager.Setup(b => b.AddBootDvd(
                    _plan, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback((VmDeploymentPlan _, string iso, CancellationToken _) => builtSeed = iso)
                .Returns(Task.CompletedTask);

            await ExecuteAsync(generation: 2);

            Assert.IsNotNull(builtSeed, "the seed must have been built and attached");
            Assert.IsTrue(File.Exists(builtSeed), "the built seed file must exist for inspection");
            byte[] isoBytes = File.ReadAllBytes(builtSeed);
            string isoText = System.Text.Encoding.ASCII.GetString(isoBytes);
            StringAssert.Contains(isoText, "packages: [htop]",
                "the user's cloud-config content must be inside the seed ISO");
        }

        [TestMethod]
        public async Task CreateVMAsync_UntaggedGen2WithoutCloudInitFile_NeverAttachesSeed()
        {
            // Regression pin: an untagged image with no --cloud-init file
            // keeps the stock flow — no seed ISO is ever built or attached.
            _customizations = new VmCustomizations();
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(false);

            await ExecuteAsync(generation: 2);

            _bootManager.Verify(h => h.AddBootDvd(
                _plan, It.Is<string>(p => p.EndsWith("_cidata.iso", StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()), Times.Never,
                "an untagged image without --cloud-init must not get a seed");
        }

        [TestMethod]
        public async Task CreateVMAsync_CloudInitFile_WithCustomSshKey_SeedUsesThatKey()
        {
            // --ssh-key + --cloud-init: post-boot SSH connects with the
            // private key matching the seed's embedded public key, so the
            // seed must embed the CUSTOM key, not the auto-generated one.
            WriteCustomUserData();
            _customizations = new VmCustomizations
            {
                CloudInitUserDataPath = _customUserDataPath,
                CustomSshPublicKeyPath = "C:\\Users\\Test\\.ssh\\custom_key.pub"
            };
            _sshKeyManager.Setup(s => s.ReadPublicKeyAsync(
                    "C:\\Users\\Test\\.ssh\\custom_key.pub", It.IsAny<CancellationToken>()))
                .ReturnsAsync("ssh-ed25519 AAAACUSTOM custom@test");
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(false);
            string? builtSeed = null;
            _bootManager.Setup(b => b.AddBootDvd(
                    _plan, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback((VmDeploymentPlan _, string iso, CancellationToken _) => builtSeed = iso)
                .Returns(Task.CompletedTask);

            await ExecuteAsync(generation: 2);

            Assert.IsNotNull(builtSeed);
            Assert.IsTrue(File.Exists(builtSeed));
            byte[] isoBytes = File.ReadAllBytes(builtSeed);
            string isoText = System.Text.Encoding.ASCII.GetString(isoBytes);
            StringAssert.Contains(isoText, "ssh-ed25519 AAAACUSTOM custom@test",
                "the seed must embed the user's custom public key");
            _sshKeyManager.Verify(s => s.ReadPublicKeyAsync(
                "C:\\Users\\Test\\.ssh\\custom_key.pub", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_TaggedCloudInitItem_WithCustomSshKey_SeedUsesThatKey()
        {
            // The same key contract on the tagged appliance path.
            _item = CloudInitItem();
            _customizations = new VmCustomizations
            {
                CustomSshPublicKeyPath = "C:\\Users\\Test\\.ssh\\custom_key.pub"
            };
            _sshKeyManager.Setup(s => s.ReadPublicKeyAsync(
                    "C:\\Users\\Test\\.ssh\\custom_key.pub", It.IsAny<CancellationToken>()))
                .ReturnsAsync("ssh-ed25519 AAAACUSTOM custom@test");
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(false);
            string? builtSeed = null;
            _bootManager.Setup(b => b.AddBootDvd(
                    _plan, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback((VmDeploymentPlan _, string iso, CancellationToken _) => builtSeed = iso)
                .Returns(Task.CompletedTask);

            await ExecuteAsync(generation: 2);

            Assert.IsNotNull(builtSeed);
            Assert.IsTrue(File.Exists(builtSeed));
            byte[] isoBytes = File.ReadAllBytes(builtSeed);
            string isoText = System.Text.Encoding.ASCII.GetString(isoBytes);
            StringAssert.Contains(isoText, "ssh-ed25519 AAAACUSTOM custom@test",
                "the seed must embed the user's custom public key");
        }

        [TestMethod]
        public async Task CreateVMAsync_CloudInitFile_MissingFile_FailsLoudly()
        {
            // The strategy re-reads the file at seed-build time; a file that
            // vanished between CLI validation and VM creation must fail the
            // deployment with the path in the message, not ship a seed
            // without the user's content.
            _customizations = new VmCustomizations
            {
                CloudInitUserDataPath = Path.Combine(Path.GetTempPath(), "vmcreate-no-such-userdata.yaml")
            };

            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => ExecuteAsync(generation: 2));
            StringAssert.Contains(ex.Message, "vmcreate-no-such-userdata.yaml");
            _lifecycleManager.Verify(m => m.StartVM(
                It.IsAny<VmDeploymentPlan>(), It.IsAny<CancellationToken>()), Times.Never,
                "a missing user-data file must fail before the VM boots");
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

        [TestMethod]
        public async Task CreateVMAsync_KvpHeartbeatNotConfirmed_ThrowsAfterPostBootRan()
        {
            // The quickspawn bug class: SSH answers but the guest's KVP
            // daemon was never started. The gate must fail the deployment
            // with diagnostics, but only AFTER post-boot: the post-boot
            // pipeline runs InstallHypervDaemonsStep (Order 280), which
            // installs and enables hv_kvp_daemon on images that ship it
            // disabled (the Arch family ships all three Hyper-V daemons
            // disabled), so gating before post-boot would break those
            // deploys the step exists to fix. The SSH witness probe then
            // confirms the daemon is truly absent in the guest before the
            // failure is raised — see the rescue test below for the
            // host-side pool lag it must be distinguished from.
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(true);
            _kvpHeartbeatGate.Setup(g => g.WaitForGuestKvpAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(async (string _, CancellationToken _, int _, int _) =>
                {
                    await Task.Delay(50); // witness-first: the immediate probe must win the race
                    return false;
                });
            _kvpHeartbeatGate.SetupGet(g => g.LastDiagnostics).Returns(new KvpGateDiagnostics
            {
                StartedUtc = DateTime.UtcNow.AddSeconds(-420),
                PollCount = 210,
                VmGuidResolved = true,
                PoolReadCount = 210
            });
            _shell.Setup(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("pgrep", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync("VMCREATE_KVP_DAEMON_ABSENT\n");
            _diagnosticsCollector.Setup(d => d.CollectAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .ReturnsAsync(new GuestDiagnostics { RawOutput = "hypervkvpd: inactive (dead)" });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ExecuteAsync(generation: 2));

            StringAssert.Contains(ex.Message, "hv_kvp_daemon", StringComparison.Ordinal,
                "RHEL/Debian binary name must be named for the fix hint");
            StringAssert.Contains(ex.Message, "hypervkvpd", StringComparison.Ordinal,
                "RHEL-class unit name must be named for the fix hint");
            StringAssert.Contains(ex.Message, _plan.VmName, StringComparison.Ordinal);
            _kvpHeartbeatGate.Verify(g => g.WaitForGuestKvpAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>()), Times.Exactly(2),
                "the gate probes cheaply once after SSH, then waits the full timeout after post-boot");
            _postBootService.Verify(p => p.RunLinuxPostBootAsync(
                It.IsAny<IGuestShell>(), It.IsAny<VmDeploymentPlan>(), It.IsAny<GalleryItem>(),
                It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ISet<string>?>()), Times.Once,
                "post-boot's main phase must run before the gate fails — it is what enables the daemon the gate checks for");
            _postBootService.Verify(p => p.RunLinuxPostBootAsync(
                It.IsAny<IGuestShell>(), It.IsAny<VmDeploymentPlan>(), It.IsAny<GalleryItem>(),
                It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), 900, It.IsAny<ISet<string>?>()), Times.Never,
                "the SSH-restore tail must not run when the gate fails");
            _diagnosticsCollector.Verify(d => d.CollectAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Once,
                "failure path must collect guest diagnostics for the log");
            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("pgrep", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()), Times.Exactly(2),
                "witness-first: one immediate probe (silent), then the failure-path probe that confirms the daemon is truly absent");
        }

        [TestMethod]
        public async Task CreateVMAsync_KvpHeartbeatEnabledByPostBoot_PassesTheGate()
        {
            // The Arch-family flow: no heartbeat immediately after SSH
            // (the daemons ship disabled), heartbeat present after
            // post-boot enabled them. This deploy must succeed end to end.
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(true);
            _kvpHeartbeatGate.Setup(g => g.WaitForGuestKvpAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), 1, 1000))
                .ReturnsAsync(false);
            _kvpHeartbeatGate.Setup(g => g.WaitForGuestKvpAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), 420, 2000))
                .ReturnsAsync(true);

            // Ordering pin: the gate sits BETWEEN the two post-boot phases —
            // the witness probe needs a live SSH session, so DisableSshStep
            // (Order 900, the tail) must run after the gate. Cross-mock
            // invocation indices cannot be compared directly, so both mocks
            // append to one shared timeline.
            var timeline = new List<string>();
            _postBootService.Setup(s => s.RunLinuxPostBootAsync(
                It.IsAny<IGuestShell>(), It.IsAny<VmDeploymentPlan>(), It.IsAny<GalleryItem>(),
                It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(), 900, It.IsAny<int>(), It.IsAny<ISet<string>?>()))
                .Callback(() => timeline.Add("postboot-main"));
            _postBootService.Setup(s => s.RunLinuxPostBootAsync(
                It.IsAny<IGuestShell>(), It.IsAny<VmDeploymentPlan>(), It.IsAny<GalleryItem>(),
                It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), 900, It.IsAny<ISet<string>?>()))
                .Callback(() => timeline.Add("postboot-restore-tail"));
            _kvpHeartbeatGate.Setup(g => g.WaitForGuestKvpAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), 420, 2000))
                .ReturnsAsync(true)
                .Callback(() => timeline.Add("gate"));

            await ExecuteAsync(generation: 2);

            _postBootService.Verify(p => p.RunLinuxPostBootAsync(
                It.IsAny<IGuestShell>(), It.IsAny<VmDeploymentPlan>(), It.IsAny<GalleryItem>(),
                It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ISet<string>?>()), Times.Exactly(2),
                "post-boot must run — InstallHypervDaemonsStep is what turns the heartbeat on for such images");
            CollectionAssert.AreEqual(
                new[] { "postboot-main", "gate", "postboot-restore-tail" }, timeline,
                "the KVP gate (and its SSH witness) must run after post-boot's main phase and before the SSH-restore tail");
            _diagnosticsCollector.Verify(d => d.CollectAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never,
                "a heartbeat that arrives after post-boot must not be treated as a failure");
        }

        [TestMethod]
        public async Task CreateVMAsync_KvpPoolsLagButDaemonRunning_WitnessRescuesAndRunsPostBoot()
        {
            // Host-side first-boot pool materialization lag (proven live:
            // daemon answering seconds after SSH, pools empty >120 s): the
            // witness probe finds the daemon alive, so the deployment must
            // proceed instead of failing a verifiably healthy guest.
            _kvpHeartbeatGate.Setup(g => g.WaitForGuestKvpAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(async (string _, CancellationToken _, int _, int _) =>
                {
                    await Task.Delay(50); // witness-first: the immediate probe must win the race
                    return false;
                });
            _kvpHeartbeatGate.SetupGet(g => g.LastDiagnostics).Returns(new KvpGateDiagnostics
            {
                StartedUtc = DateTime.UtcNow.AddSeconds(-420),
                PollCount = 210,
                VmGuidResolved = true,
                PoolReadCount = 210
            });
            _shell.Setup(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("pgrep", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync("VMCREATE_KVP_DAEMON_RUNNING\n");
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(true);

            await ExecuteAsync(generation: 2);

            _postBootService.Verify(p => p.RunLinuxPostBootAsync(
                It.IsAny<IGuestShell>(), It.IsAny<VmDeploymentPlan>(), It.IsAny<GalleryItem>(),
                It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ISet<string>?>()), Times.Exactly(2),
                "a verifiably healthy guest must proceed through post boot, including the SSH-restore tail");
            _diagnosticsCollector.Verify(d => d.CollectAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never,
                "the rescue path is a warning, not a failure — no diagnostics collection");
            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("pgrep", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()), Times.Once,
                "the immediate witness probe rescues the healthy guest before any failure path runs");
        }

        [TestMethod]
        public async Task CreateVMAsync_KvpHeartbeatNotConfirmed_WitnessTransportError_FailsWithDistinctMessage()
        {
            // The witness now runs BEFORE DisableSshStep restores sshd, so a
            // transport failure is no longer the expected "SSH was just
            // restored" case — it means the session genuinely broke and the
            // deployment must fail (fail-safe contract restored). The error
            // must say the probe could not REACH the guest, distinct from
            // the daemon-absent message.
            _postBootService.Setup(s => s.HasLinuxPostBootSteps(_item, _customizations)).Returns(true);
            _kvpHeartbeatGate.Setup(g => g.WaitForGuestKvpAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(false);
            _shell.Setup(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("pgrep", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("SSH command failed (exit code 255): Connection refused"));
            _diagnosticsCollector.Setup(d => d.CollectAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .ReturnsAsync(new GuestDiagnostics { RawOutput = "[unreachable]" });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ExecuteAsync(generation: 2));

            StringAssert.Contains(ex.Message, "could not reach the guest", StringComparison.Ordinal,
                "transport failure must be reported as unreachable, not as daemon-absent");
            _postBootService.Verify(p => p.RunLinuxPostBootAsync(
                It.IsAny<IGuestShell>(), It.IsAny<VmDeploymentPlan>(), It.IsAny<GalleryItem>(),
                It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ISet<string>?>()), Times.Once,
                "only the main phase runs — the SSH-restore tail must not run after a gate failure");
        }

        [TestMethod]
        public async Task CreateVMAsync_IntegrationServicesDisabled_SkipsHeartbeatGateAndWitness()
        {
            // Opting out of integration services also opts out of the gate
            // and its witness — the user explicitly accepted a degraded
            // integration stack.
            _customizations.EnableIntegrationServices = false;
            _kvpHeartbeatGate.Setup(g => g.WaitForGuestKvpAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>()))
                .ThrowsAsync(new InvalidOperationException("gate must not be called"));

            await ExecuteAsync(generation: 2);

            _kvpHeartbeatGate.Verify(g => g.WaitForGuestKvpAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("pgrep", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()), Times.Never,
                "the witness probe must not run when integration services are disabled");
        }

        [TestMethod]
        public async Task CreateVMAsync_FullDisk_FailsFastBeforeAnyVmCreation()
        {
            // The shipped failure: the clone filled the VM-disk drive mid-
            // write; Hyper-V paused the VM (Disk Full) and the deploy died
            // as an SSH timeout. The pre-flight must reject a full drive
            // BEFORE the VM skeleton or any disk is created, with an error
            // naming the drive.
            _strategy.FreeSpaceProbe = _ => (4L * 1024 * 1024 * 1024, "D:\\");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ExecuteAsync(generation: 1));

            StringAssert.Contains(ex.Message, "D:\\");
            StringAssert.Contains(ex.Message, "Not enough free space", StringComparison.Ordinal);
            _lifecycleManager.Verify(m => m.CreateVMAsync(
                It.IsAny<VmDeploymentPlan>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()), Times.Never,
                "a disk-full rejection must not leave a half-created VM behind");
            _diskManager.Verify(m => m.AddNewHardDrive(
                It.IsAny<VmDeploymentPlan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateVMAsync_EnoughDisk_ProceedsToVmCreation()
        {
            // The guard passes silently and does not disturb the normal flow.
            _strategy.FreeSpaceProbe = _ => (2L * 1024 * 1024 * 1024 * 1024, "D:\\");
            _stepPlanner
                .Setup(p => p.NeedsIsoBootCycle(It.IsAny<GalleryItem?>(), It.IsAny<VmCustomizations?>()))
                .Returns(false);

            await ExecuteAsync(generation: 2);

            _lifecycleManager.Verify(m => m.CreateVMAsync(
                It.IsAny<VmDeploymentPlan>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()), Times.Once,
                "a passing pre-flight must not block the deployment");
        }

        [TestMethod]
        public async Task CreateVMAsync_UnverifiedIso_FailsBeforeBootDvdAttach()
        {
            // The pre-boot check: the cached manifest's signature and the
            // ISO's hash are re-verified right before the VM boots from
            // the ISO. A failure must stop the deploy before the DVD is
            // attached or the VM started.
            _cloningIsoDownloader.Setup(d => d.VerifyCachedIsoGuardedAsync(
                    It.IsAny<VmDeploymentPlan>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException(
                    "The cloning ISO has no valid signed manifest — it cannot be trusted for boot."));

            await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(generation: 2));

            _bootManager.Verify(m => m.AddBootDvd(
                It.IsAny<VmDeploymentPlan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
                "an unverified ISO must never be attached");
            _lifecycleManager.Verify(m => m.StartVM(
                It.IsAny<VmDeploymentPlan>(), It.IsAny<CancellationToken>()), Times.Never,
                "an unverified ISO must never boot");
        }

        [TestMethod]
        public async Task CreateVMAsync_IsoBootCycle_HoldsReadGuardUntilTheEnd()
        {
            // The verification returns a read guard; the strategy must hold
            // it for the whole deployment (the ISO stays attached during
            // the boot cycle and post-boot) and dispose it at the end.
            var guard = new Mock<IIsoReadGuard>();
            _cloningIsoDownloader.Setup(d => d.VerifyCachedIsoGuardedAsync(
                    It.IsAny<VmDeploymentPlan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(guard.Object);

            await ExecuteAsync(generation: 1);

            guard.Verify(g => g.Dispose(), Times.Once, "the read guard must be released when the deployment ends");
        }

        [TestMethod]
        public async Task CreateVMAsync_IsoBootCycle_VerifiesBeforeAttach()
        {
            // The check runs on the Gen-1 clone path too, ordered before
            // the DVD attach.
            var order = new List<string>();
            _cloningIsoDownloader.Setup(d => d.VerifyCachedIsoGuardedAsync(
                    It.IsAny<VmDeploymentPlan>(), It.IsAny<CancellationToken>()))
                .Callback(() => order.Add("Verify")).ReturnsAsync(Mock.Of<IIsoReadGuard>());
            _bootManager.Setup(m => m.AddBootDvd(
                    It.IsAny<VmDeploymentPlan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback(() => order.Add("AddBootDvd")).Returns(Task.CompletedTask);

            await ExecuteAsync(generation: 1);

            CollectionAssert.Contains(order, "Verify");
            Assert.AreEqual("Verify", order[0], "verification must happen before the ISO is attached");
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
