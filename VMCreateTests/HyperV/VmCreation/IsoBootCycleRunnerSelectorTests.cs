using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV.VmCreation;

namespace VMCreate.Tests.HyperV.VmCreation
{
    /// <summary>
    /// W4 fix tests: the selector reads the PLAN's IsoVsockEnabled (one
    /// source of truth per deployment — stamped by CreateVM.BuildPlan, so
    /// the GUI DeployPage and CLI create paths carry the AppSettings flag
    /// without knowing about it), and the VMCREATE_ISO_PATH interlock skip
    /// consults the same frozen plan state.
    /// </summary>
    /// <remarks>[DoNotParallelize]: these tests set VMCREATE_ISO_PATH, which
    /// plan construction now resolves — parallel neighbours would freeze the
    /// wrong override into their plans.</remarks>
    [TestClass]
    [DoNotParallelize]
    public class IsoBootCycleRunnerSelectorTests
    {
        private static (IsoBootCycleRunnerSelector Selector, Mock<ILegacyIsoBootCycleRunner> Legacy, Mock<IVsockIsoBootCycleRunner> Vsock) Create()
        {
            var legacy = new Mock<ILegacyIsoBootCycleRunner>();
            var vsock = new Mock<IVsockIsoBootCycleRunner>();
            legacy.Setup(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(IsoBootCycleResult.Succeeded());
            vsock.Setup(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(IsoBootCycleResult.Succeeded());
            return (new IsoBootCycleRunnerSelector(legacy.Object, vsock.Object,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IsoBootCycleRunnerSelector>.Instance), legacy, vsock);
        }

        private static VmCreationContext Context(VmDeploymentPlan plan) =>
            new(plan, new VmCustomizations(), "source.img",
                new GalleryItem { Name = "TestImage", IsNativeHyperV = false, IsWindows = false },
                new global::VMCreate.MediaHandlers.MediaPreparationResult("media.vhdx", 2), CancellationToken.None,
                new Progress<CreateVMProgressInfo>());

        /// <summary>Creates a plan with the flag and writes a fake v2 cache
        /// (ISO + .version sidecar) at the plan's EFFECTIVE ISO path.</summary>
        private static (VmDeploymentPlan Plan, string Iso, string Sidecar) PlanWithSidecar(string version)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"vmcreate-sel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var plan = VmDeploymentPlan.FromSettings(new VmSettings
            {
                VMName = "t",
                CloningIsoPath = Path.Combine(dir, "hyperv-convert.iso"),
            }) with { IsoVsockEnabled = true };
            string iso = plan.CloningIsoV2Path; // effective when the flag is on
            File.WriteAllText(iso, "x");
            File.WriteAllText(iso + ".version", version);
            return (plan, iso, iso + ".version");
        }

        [TestMethod]
        public async Task PlanFlagOn_WithV2Sidecar_PicksVsockRunner()
        {
            var (plan, iso, sidecar) = PlanWithSidecar("2.0.0-alpha.1");
            try
            {
                var (selector, legacy, vsock) = Create();

                await selector.RunAsync(Context(plan), 2, "media.vhdx", new VmCustomizations(),
                    new Progress<CreateVMProgressInfo>(), CancellationToken.None);

                vsock.Verify(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
                legacy.Verify(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Never);
            }
            finally
            {
                File.Delete(iso);
                File.Delete(sidecar);
                Directory.Delete(Path.GetDirectoryName(iso)!);
            }
        }

        [TestMethod]
        public async Task PlanFlagOff_PicksLegacyRunner_EvenWith2xSidecar()
        {
            // The selector must consult the PLAN, not AppSettings: a plan
            // built with the flag off runs legacy even when the v2 ISO
            // happens to be cached.
            var (plan, iso, sidecar) = PlanWithSidecar("2.0.0-alpha.1");
            try
            {
                var flagOffPlan = plan with { IsoVsockEnabled = false };
                var (selector, legacy, vsock) = Create();

                await selector.RunAsync(Context(flagOffPlan), 2, "media.vhdx", new VmCustomizations(),
                    new Progress<CreateVMProgressInfo>(), CancellationToken.None);

                legacy.Verify(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
                vsock.Verify(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Never);
            }
            finally
            {
                File.Delete(iso);
                File.Delete(sidecar);
                Directory.Delete(Path.GetDirectoryName(iso)!);
            }
        }

        [TestMethod]
        public async Task PlanFlagOn_Legacy1xSidecar_FallsBackToLegacyRunner()
        {
            // Sidecar at the EFFECTIVE (v2 cache) path says 1.x — e.g. a
            // pre-W4 cache or a manual copy: fall back, loudly.
            var (plan, iso, sidecar) = PlanWithSidecar("1.1.4");
            try
            {
                var (selector, legacy, vsock) = Create();

                await selector.RunAsync(Context(plan), 2, "media.vhdx", new VmCustomizations(),
                    new Progress<CreateVMProgressInfo>(), CancellationToken.None);

                legacy.Verify(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
                vsock.Verify(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Never);
            }
            finally
            {
                File.Delete(iso);
                File.Delete(sidecar);
                Directory.Delete(Path.GetDirectoryName(iso)!);
            }
        }

        [TestMethod]
        public async Task EnvOverrideActive_SkipsInterlock_PicksVsock()
        {
            // A locally built ISO has no sidecar; with the override active
            // on the plan the interlock must not read (and reject) it.
            string localIso = Path.Combine(Path.GetTempPath(), $"vmcreate-sel-{Guid.NewGuid():N}.iso");
            File.WriteAllText(localIso, "x");
            Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", localIso);
            try
            {
                var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "t" }) with { IsoVsockEnabled = true };
                Assert.IsTrue(plan.IsIsoPathOverridden, "fixture sanity: no sidecar exists — the interlock would reject");
                var (selector, legacy, vsock) = Create();

                await selector.RunAsync(Context(plan), 2, "media.vhdx", new VmCustomizations(),
                    new Progress<CreateVMProgressInfo>(), CancellationToken.None);

                vsock.Verify(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
                legacy.Verify(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Never);
            }
            finally
            {
                Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", null);
                File.Delete(localIso);
            }
        }

        [TestMethod]
        public async Task EnvOverrideActive_FlagOff_PicksLegacy_WithWarning()
        {
            // Reverse mistake: a 2.x ISO behind the override but the flag
            // off — the legacy runner would boot it and wait for KVP
            // progress that never arrives. Legacy still runs (the flag is
            // the source of truth), but the selector warns.
            string localIso = Path.Combine(Path.GetTempPath(), $"vmcreate-sel-{Guid.NewGuid():N}.iso");
            File.WriteAllText(localIso, "x");
            Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", localIso);
            try
            {
                var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "t" });
                var (selector, legacy, vsock) = Create();

                await selector.RunAsync(Context(plan), 2, "media.vhdx", new VmCustomizations(),
                    new Progress<CreateVMProgressInfo>(), CancellationToken.None);

                legacy.Verify(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
                vsock.Verify(r => r.RunAsync(It.IsAny<VmCreationContext>(), It.IsAny<int>(), It.IsAny<string>(),
                    It.IsAny<VmCustomizations>(), It.IsAny<IProgress<CreateVMProgressInfo>>(), It.IsAny<CancellationToken>()), Times.Never);
            }
            finally
            {
                Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", null);
                File.Delete(localIso);
            }
        }

        [TestMethod]
        public void StaleEnvOverride_IsNotActive_InterlockStillApplies()
        {
            // A stale VMCREATE_ISO_PATH (file missing) must NOT disable the
            // interlock: the plan ignores it, so the selector must too.
            Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", @"C:\definitely\not\here.iso");
            try
            {
                var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "t" }) with { IsoVsockEnabled = true };
                Assert.IsFalse(plan.IsIsoPathOverridden, "stale override must not be active on the plan");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", null);
            }
        }

        [TestMethod]
        public void BuildPlan_StampAppSettingsFlag_CarriesThroughBothFrontEnds()
        {
            // Both front ends (GUI DeployPage, CLI create) call
            // StartCreateVMAsync, which builds the plan via BuildPlan — the
            // ONE place AppSettings.IsoVsockEnabled enters a deployment.
            // Front-end VmSettings never carry the flag; the stamp must
            // override whatever default they have.
            var userSettings = new VmSettings { VMName = "t" }; // GUI/CLI-built: flag never set

            var planOn = CreateVM.BuildPlan(userSettings, isoVsockEnabled: true, effectiveVmName: "t_20260101");
            Assert.IsTrue(planOn.IsoVsockEnabled, "flag on in AppSettings → plan carries it");
            Assert.AreEqual("t_20260101", planOn.VmName);
            Assert.AreEqual(planOn.CloningIsoV2Path, planOn.EffectiveCloningIsoPath);

            var planOff = CreateVM.BuildPlan(userSettings, isoVsockEnabled: false, effectiveVmName: "t_20260101");
            Assert.IsFalse(planOff.IsoVsockEnabled);
            Assert.AreEqual(planOff.CloningIsoPath, planOff.EffectiveCloningIsoPath);
        }
    }
}
