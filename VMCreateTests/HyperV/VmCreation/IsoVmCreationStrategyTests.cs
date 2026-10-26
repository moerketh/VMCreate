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
        // Fixture fields — assigned in [TestInitialize] Setup() before every test.
        private Mock<IVmLifecycleManager> _lifecycleManager = null!;
        private Mock<IVmDiskManager> _diskManager = null!;
        private Mock<IVmBootManager> _bootManager = null!;
        private Mock<IVmNetworkManager> _networkManager = null!;
        private Mock<IVmConfigManager> _configManager = null!;
        private Mock<IGuestShellFactory> _guestShellFactory = null!;
        private Mock<ISshKeyManager> _sshKeyManager = null!;
        private Mock<IPostBootCustomizationService> _postBootService = null!;
        private IsoVmCreationStrategy _strategy = null!;
        private VmSettings _vmSettings = null!;
        private VmDeploymentPlan _plan = null!;
        private VmCustomizations _customizations = null!;
        private GalleryItem _item = null!;
        private string _isoPath = null!;
        private string _vhdxPath = null!;
        private readonly List<string> _logs = new();

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
            _postBootService = new Mock<IPostBootCustomizationService>();

            _vmSettings = new VmSettings { VMName = "TestVM", MemoryInMB = 4096 };
            _plan = VmDeploymentPlan.FromSettings(_vmSettings);
            _customizations = new VmCustomizations();
            _item = new GalleryItem { Name = "TestISO", IsWindows = false };
            _isoPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".iso");
            File.WriteAllBytes(_isoPath, new byte[0]);
            _vhdxPath = Path.Combine(Path.GetTempPath(), "vmcreate-iso-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_vhdxPath);
            _logs.Clear();

            var loggerMock = new Mock<ILogger<IsoVmCreationStrategy>>();
            loggerMock
                .Setup(l => l.Log<It.IsAnyType>(
                    It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback(new InvocationAction(inv =>
                {
                    var ex = inv.Arguments[3] as Exception;
                    _logs.Add((inv.Arguments[2]?.ToString() ?? "") + (ex != null ? $" :: {ex.GetType().Name}: {ex.Message}" : ""));
                }));

            _strategy = new IsoVmCreationStrategy(
                _lifecycleManager.Object,
                _diskManager.Object,
                _bootManager.Object,
                _networkManager.Object,
                _configManager.Object,
                _guestShellFactory.Object,
                _sshKeyManager.Object,
                _postBootService.Object,
                loggerMock.Object,
                Mock.Of<IVmPathService>(s =>
                    s.DefaultVmPath == @"C:\Hyper-V" &&
                    s.DefaultVhdxPath == _vhdxPath));

            var shell = new Mock<IGuestShell>();
            shell.Setup(s => s.WaitForReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _guestShellFactory.Setup(f => f.CreateForWindows(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(shell.Object);
            _guestShellFactory.Setup(f => f.Create(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(shell.Object);
            _guestShellFactory.Setup(f => f.CreateWithPinnedIp(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(shell.Object);
            _sshKeyManager.Setup(k => k.EnsureKeyPairAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync("ssh-ed25519 AAAATESTKEY vmcreate@test");
            _sshKeyManager.Setup(k => k.ReadPublicKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("ssh-ed25519 AAAACUSTOMKEY vmcreate@test");
            _sshKeyManager.Setup(k => k.GetPrivateKeyPath(It.IsAny<string?>()))
                .Returns(@"C:\Keys\id_ed25519_vmcreate");
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { File.Delete(_isoPath); } catch { }
            // The seed ISO can still be lock-releasing when a test fails
            // mid-flight — retry briefly before leaving the temp dir behind.
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (Directory.Exists(_vhdxPath)) Directory.Delete(_vhdxPath, true);
                    break;
                }
                catch (IOException) when (i < 4)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (i < 4)
                {
                    Thread.Sleep(100);
                }
                catch { break; }
            }
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

            _diskManager.Verify(h => h.AddNewHardDrive(_plan, _vhdxPath, It.IsAny<CancellationToken>()), Times.Once);
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
        public async Task CreateVMAsync_SetsEnhancedSessionTransportBeforeFirstStart()
        {
            // Hyper-V only polls the guest's vsock:3389 listener on a worker
            // STARTED with the HvSocket transport; setting it on a running VM
            // leaves Enhanced Session unavailable until a host restart.
            var calls = new List<string>();
            _configManager.Setup(c => c.SetEnhancedSession(_plan, It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("SetEnhancedSession")).Returns(Task.CompletedTask);
            _lifecycleManager.Setup(l => l.StartVM(_plan, It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("StartVM")).Returns(Task.CompletedTask);

            await ExecuteAsync();

            CollectionAssert.Contains(calls, "SetEnhancedSession");
            Assert.AreEqual("SetEnhancedSession", calls[0],
                $"the transport must be set before the first StartVM (calls: {string.Join(", ", calls)})");
        }

        [TestMethod]
        public async Task CreateVMAsync_ConnectsNetworkAdapter()
        {
            await ExecuteAsync();
            _networkManager.Verify(h => h.ConnectNetworkAdapter(_plan, It.IsAny<CancellationToken>()), Times.Once);
        }

        // ── Omarchy (Arch) cidata branch ────────────────────────────────

        private static GalleryItem CreateOmarchyItem() => new()
        {
            Name = "Omarchy",
            IsWindows = false,
            LinuxDistro = LinuxDistro.Arch,
            InitialUsername = "omarchy",
            InitialPassword = "test-password",
            Tags = new System.Collections.Generic.List<string> { "omarchy" },
        };

        [TestMethod]
        public async Task CreateVMAsync_ArchDistro_WithoutOmarchyTag_DoesNotGetCidataSeed()
        {
            // Regression pin for the Athena OS misroute: the cidata branch is
            // gated on the omarchy tag, not the Arch distro hint. Athena OS
            // (Arch) and any --image-uri Arch ISO share LinuxDistro.Arch but
            // install manually — an Omarchy seed there would impose a 20 GB
            // disk floor and wait 40 minutes for an sshd that never comes up.
            _item = new GalleryItem
            {
                Name = "Athena OS (Arch)",
                IsWindows = false,
                LinuxDistro = LinuxDistro.Arch,
                InitialUsername = "athena",
                InitialPassword = "athena",
                Tags = new System.Collections.Generic.List<string> { "athena-arch" },
            };

            await ExecuteAsync();

            _bootManager.Verify(b => b.AttachIsoToNewDvdDrive(It.IsAny<VmDeploymentPlan>(), It.Is<string>(p => p.EndsWith("_cidata.iso")), It.IsAny<CancellationToken>()), Times.Never);
            _bootManager.Verify(b => b.SetFirstBootToDvd(_plan, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(b => b.SetFirstBootToHardDrive(_plan, It.IsAny<CancellationToken>()), Times.Never);
            _guestShellFactory.Verify(f => f.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateVMAsync_Omarchy_BuildsAndAttachesCidataSeed()
        {
            _item = CreateOmarchyItem();
            string seedPath = Path.Combine(_vhdxPath, $"{_plan.VmName}_cidata.iso");
            string? attachedSeed = null;
            _bootManager
                .Setup(b => b.AttachIsoToNewDvdDrive(_plan, It.Is<string>(p => p.EndsWith("_cidata.iso")), It.IsAny<CancellationToken>()))
                .Callback<VmDeploymentPlan, string, CancellationToken>((_, path, _) => attachedSeed = path)
                .Returns(Task.CompletedTask);

            await ExecuteAsync();

            Assert.AreEqual(seedPath, attachedSeed, $"cidata seed ISO must be built at the expected path and attached. Logs: {string.Join(" | ", _logs)}");
        }

        [TestMethod]
        public async Task CreateVMAsync_Omarchy_CidataSeedGoesToDedicatedDrive_InstallerStaysAttached()
        {
            // Regression pin for the PXE stall: attaching the seed through AddBootDvd
            // retargeted the single DVD drive, swapping the installer ISO for the
            // non-bootable seed — HDD-first then fell through to PXE.
            _item = CreateOmarchyItem();
            string seedPath = Path.Combine(_vhdxPath, $"{_plan.VmName}_cidata.iso");

            await ExecuteAsync();

            _bootManager.Verify(b => b.AddBootDvd(_plan, _isoPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(b => b.AttachIsoToNewDvdDrive(_plan, seedPath, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(b => b.AddBootDvd(_plan, seedPath, It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateVMAsync_Omarchy_SetsFirstBootToHardDrive()
        {
            _item = CreateOmarchyItem();

            await ExecuteAsync();

            _bootManager.Verify(b => b.SetFirstBootToHardDrive(_plan, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(b => b.SetFirstBootToDvd(_plan, It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateVMAsync_Omarchy_WritesLoginNotes()
        {
            _item = CreateOmarchyItem();

            await ExecuteAsync();

            _configManager.Verify(c => c.SetVMLoginNotes(_plan, "omarchy", "test-password", It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_Omarchy_WaitsForSshAsSeedUser_AndCleansUpSeed()
        {
            _item = CreateOmarchyItem();
            string seedPath = Path.Combine(_vhdxPath, $"{_plan.VmName}_cidata.iso");

            await ExecuteAsync();

            _guestShellFactory.Verify(f => f.Create(_plan.VmName, @"C:\Keys\id_ed25519_vmcreate", "omarchy"), Times.Once);
            _bootManager.Verify(b => b.RemoveBootDvd(_plan, seedPath, It.IsAny<CancellationToken>()), Times.Once);
            Assert.IsFalse(File.Exists(seedPath), "cidata seed ISO must be deleted after the wait");
        }

        [TestMethod]
        public async Task CreateVMAsync_Omarchy_StaticIp_WaitsOverPinnedAddress()
        {
            // The installed Omarchy system is statically configured when the
            // deployment pinned an address; DHCP-lease-driven discovery may
            // never see it, so the post-install SSH wait targets the pinned
            // address (host part of the CIDR) as the seeded user.
            _item = CreateOmarchyItem();
            _customizations = new VmCustomizations { StaticIp = "192.168.10.50/24" };

            await ExecuteAsync();

            _guestShellFactory.Verify(f => f.CreateWithPinnedIp(
                _plan.VmName, @"C:\Keys\id_ed25519_vmcreate", "omarchy", "192.168.10.50"), Times.Once,
                "the pinned address (host part of the CIDR) is the SSH target for the installed system");
            _guestShellFactory.Verify(f => f.Create(_plan.VmName, It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "discovery mode must not be used for a statically configured guest");
        }

        [TestMethod]
        public async Task CreateVMAsync_ManualIso_StaticIp_FailsLoudly()
        {
            // Every non-Omarchy ISO deploy is a manual install: no seed ever
            // carries the static address and no deployment SSH exists to
            // target it — the flags would be silently ignored. Fail before
            // the VM skeleton is created.
            _customizations = new VmCustomizations { StaticIp = "192.168.10.50/24" };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync());

            StringAssert.Contains(ex.Message, "--static-ip");
            _lifecycleManager.Verify(m => m.CreateVMAsync(
                It.IsAny<VmDeploymentPlan>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()), Times.Never,
                "the unsupported combination must fail before any VM work");
        }

        [TestMethod]
        public async Task CreateVMAsync_Omarchy_RunsLinuxPostBootAfterSsh()
        {
            // Omarchy joins the shared Linux post-boot pipeline: the pipeline's
            // order-45 passwordless-sudo bootstrap (omarchy-tagged) makes every
            // later sudo-based step work on the installed system's passworded
            // sudo — the historical blocker for running these steps here.
            _item = CreateOmarchyItem();

            await ExecuteAsync();

            _postBootService.Verify(s => s.RunLinuxPostBootAsync(
                It.IsAny<IGuestShell>(), It.IsAny<VmDeploymentPlan>(), It.IsAny<GalleryItem>(),
                It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [TestMethod]
        public async Task CreateVMAsync_Omarchy_CustomPublicKey_SeededForKeyAuth()
        {
            _item = CreateOmarchyItem();
            _customizations.CustomSshPublicKeyPath = @"C:\Keys\custom.pub";

            string? attachedSeed = null;
            long attachedLength = -1;
            _bootManager
                .Setup(b => b.AttachIsoToNewDvdDrive(_plan, It.Is<string>(p => p.EndsWith("_cidata.iso")), It.IsAny<CancellationToken>()))
                .Callback<VmDeploymentPlan, string, CancellationToken>((_, path, _) =>
                {
                    attachedSeed = path;
                    attachedLength = new FileInfo(path).Length;
                })
                .Returns(Task.CompletedTask);

            await ExecuteAsync();

            _sshKeyManager.Verify(k => k.ReadPublicKeyAsync(@"C:\Keys\custom.pub", It.IsAny<CancellationToken>()), Times.Once);
            _sshKeyManager.Verify(k => k.EnsureKeyPairAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.IsNotNull(attachedSeed);
            Assert.IsTrue(attachedLength > 0, "seed ISO must have been burned");
        }

        [TestMethod]
        public async Task CreateVMAsync_Omarchy_SeedFailure_FallsBackToDvdBoot_WithoutLoginNotes()
        {
            _item = CreateOmarchyItem();
            _sshKeyManager.Setup(k => k.EnsureKeyPairAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("disk full"));

            await ExecuteAsync();

            _bootManager.Verify(b => b.SetFirstBootToDvd(_plan, It.IsAny<CancellationToken>()), Times.Once);
            _bootManager.Verify(b => b.SetFirstBootToHardDrive(_plan, It.IsAny<CancellationToken>()), Times.Never);
            _configManager.Verify(c => c.SetVMLoginNotes(_plan, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            _guestShellFactory.Verify(f => f.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateVMAsync_Omarchy_DriveBelowMinimum_FailsBeforeFallback()
        {
            // A misconfigured plan must fail loudly — the seed-failure fallback
            // cannot save a VM whose disk cannot hold an Omarchy install.
            _item = CreateOmarchyItem();
            _plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM", NewDriveSizeInGB = 0 });

            var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(ExecuteAsync);
            StringAssert.Contains(ex.Message, "below the Omarchy minimum");

            _bootManager.Verify(b => b.SetFirstBootToDvd(It.IsAny<VmDeploymentPlan>(), It.IsAny<CancellationToken>()), Times.Never);
            _bootManager.Verify(b => b.SetFirstBootToHardDrive(It.IsAny<VmDeploymentPlan>(), It.IsAny<CancellationToken>()), Times.Never);
            _guestShellFactory.Verify(f => f.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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
