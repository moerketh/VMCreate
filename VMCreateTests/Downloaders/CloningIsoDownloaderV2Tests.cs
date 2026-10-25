using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace VMCreate.Tests.Downloaders
{
    /// <summary>
    /// W4 tests: the v2 release-selection policy (semver order, stable
    /// beats prerelease, array order never trusted). Network-free: the
    /// comparator is pure.
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
        public void Compare_PrereleaseNumericIdentifiers_NotLexical()
        {
            // Semver compares dot-separated numeric identifiers numerically:
            // a lexical compare would sort alpha.10 BELOW alpha.9 ("1" < "9")
            // and quietly roll the channel back to the tenth alpha.
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v2.0.0-alpha.10", "", "v2.0.0-alpha.9", "") > 0,
                "alpha.10 must outrank alpha.9");
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v2.0.0-alpha.2", "", "v2.0.0-alpha.10", "") < 0);
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v2.0.0-rc.10", "", "v2.0.0-rc.9", "") > 0);
        }

        [TestMethod]
        public void Compare_PrereleaseSemverEdgeCases()
        {
            // Fewer identifiers sort lower when the shared prefix ties.
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v2.0.0-alpha.1", "", "v2.0.0-alpha", "") > 0,
                "alpha.1 > alpha");
            // Numeric identifiers rank below alphanumeric ones (semver 11.4).
            Assert.IsTrue(CloningIsoDownloader.CompareCandidates("v2.0.0-alpha.x", "", "v2.0.0-alpha.1", "") > 0,
                "alpha.x > alpha.1");
            // Non-numeric identifiers still compare lexically (alpha < beta).
            Assert.IsTrue(CloningIsoDownloader.ComparePrereleaseSuffixes("beta.1", "alpha.99") > 0);
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
    }
}
