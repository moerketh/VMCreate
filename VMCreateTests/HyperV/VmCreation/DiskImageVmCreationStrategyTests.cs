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

            // Cloud-init seed builds need a real public key line.
            _sshKeyManager.Setup(s => s.EnsureKeyPairAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync("ssh-ed25519 AAAAC3Nzatest vmcreate@test");

            // Shared guest shell mock: tests can setup RunCommandAsync for
            // witness probes (KVP gate timeout) and shell-dependent steps.
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.WaitForReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _guestShellFactory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<string>())).Returns(_shell.Object);
            _sshKeyManager.Setup(s => s.GetPrivateKeyPath(It.IsAny<string>())).Returns("C:\\Users\\Test\\.ssh\\id_rsa");

            // KVP heartbeat gate: pass-through default (guest daemon alive).
            // Loose mocks also produce a null LastDiagnostics — the strategy
            // must tolerate that.
            _kvpHeartbeatGate.Setup(g => g.WaitForGuestKvpAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(true);

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

            _diskManager.Verify(h => h.AddNewHardDrive(_plan, @"C:\Hyper-V\Virtual hard disks", It.IsAny<CancellationToken>()), Times.Once);
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

        [TestMethod]
        public async Task CreateVMAsync_CloudInitGen1_StillClonesLayoutWithSeed()
        {
            // A hypothetical cloud-init image WITHOUT an ESP (Ubuntu-class):
            // the clone cycle must still convert the layout, with the seed
            // attached alongside for the post-clone first boot.
            _item = CloudInitItem();
            _customizations = new VmCustomizations();

            await ExecuteAsync(generation: 1);

            _diskManager.Verify(h => h.AddNewHardDrive(_plan, @"C:\Hyper-V\Virtual hard disks", It.IsAny<CancellationToken>()), Times.Once);
            _diskManager.Verify(h => h.AddExistingHardDrive(_plan, _mediaPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.AddBootDvd(_plan, _cloningIsoPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(h => h.AddBootDvd(_plan, It.Is<string>(p => p.EndsWith("_cidata.iso", StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()), Times.Once);
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
            StringAssert.Contains(ex.Message, "TestVM", StringComparison.Ordinal);
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
