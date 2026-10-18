using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace VMCreate.Tests.Downloaders
{
    /// <summary>
    /// W4 tests: the v2 release-selection policy (semver order, stable
    /// beats prerelease, array order never trusted) and the split cache
    /// paths. Network-free: the comparator is pure and the path logic is
    /// plan-level.
    /// </summary>
    [TestClass]
    public class CloningIsoDownloaderV2Tests
    {
        #region Semver / candidate ordering

        [TestMethod]
        public void Compare_NumericSemver_NotLexical()
        {
            // The reason array order can't be trusted and string compare
            // can't be used: 2.10.0 vs 2.9.1.
            int cmp = CloningIsoDownloader.CompareCandidates("v2.10.0", "", "v2.9.1", "");
            Assert.IsTrue(cmp > 0, "2.10.0 must outrank 2.9.1");
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v2.0.1", "", "v2.0.0", "") > 0);
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v3.0.0", "", "v2.99.99", "") > 0);
        }

        [TestMethod]
        public void Compare_StableOutranksPrereleaseOfSameCore()
        {
            // 2.0.0 beats 2.0.0-alpha.1 even though the prerelease was
            // published later.
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v2.0.0", "2026-01-01", "v2.0.0-alpha.1", "2026-12-31") > 0);
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v2.0.0-alpha.1", "2026-12-31", "v2.0.0", "2026-01-01") < 0);
        }

        [TestMethod]
        public void Compare_PrereleaseSuffixOrder()
        {
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v2.0.0-beta.1", "", "v2.0.0-alpha.9", "") > 0);
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v2.0.0-rc.1", "", "v2.0.0-beta.1", "") > 0);
        }

        [TestMethod]
        public void Compare_HigherCorePrerelease_BeatsLowerCoreStable()
        {
            // A 2.1.0-alpha outranks 2.0.0 stable: higher semver core wins;
            // the stable-preference gates the pick loop within equal cores.
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v2.1.0-alpha.1", "", "v2.0.0", "") > 0);
        }

        [TestMethod]
        public void Compare_IdenticalStableTags_Tie()
        {
            Assert.AreEqual(0, CloningIsoDownloader.CompareCandidates("v2.0.0", "a", "v2.0.0", "b"));
        }

        #endregion

        #region Plan-level cache separation

        [TestMethod]
        public void V2CachesAtItsOwnPath_SidecarPairsWithIt()
        {
            var settings = new VmSettings { VMName = "t", IsoVsockEnabled = true };
            var plan = VmDeploymentPlan.FromSettings(settings);

            Assert.AreNotEqual(plan.CloningIsoPath, plan.CloningIsoV2Path, "v2 must not share the legacy cache file");
            Assert.AreEqual(Path.GetDirectoryName(plan.CloningIsoPath), Path.GetDirectoryName(plan.CloningIsoV2Path), "same directory");
            Assert.AreEqual(plan.CloningIsoV2Path, plan.EffectiveCloningIsoPath, "flag on → v2 path effective");
        }

        [TestMethod]
        public void FlagOff_UsesLegacyPath()
        {
            var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "t" });
            Assert.AreEqual(plan.CloningIsoPath, plan.EffectiveCloningIsoPath);
        }

        [TestMethod]
        public void EnvOverride_Wins_WhenFileExists()
        {
            string overridePath = Path.Combine(Path.GetTempPath(), $"vmcreate-test-override-{Guid.NewGuid():N}.iso");
            File.WriteAllText(overridePath, "x");
            try
            {
                Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", overridePath);
                var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "t", IsoVsockEnabled = true });
                Assert.AreEqual(overridePath, plan.EffectiveCloningIsoPath, "dev override must win over both caches");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", null);
                File.Delete(overridePath);
            }
        }

        [TestMethod]
        public void EnvOverride_Ignored_WhenFileMissing()
        {
            Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", @"C:\definitely\not\here.iso");
            try
            {
                var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "t", IsoVsockEnabled = true });
                Assert.AreEqual(plan.CloningIsoV2Path, plan.EffectiveCloningIsoPath, "a stale override must not break deploys");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VMCREATE_ISO_PATH", null);
            }
        }

        #endregion
    }
}
