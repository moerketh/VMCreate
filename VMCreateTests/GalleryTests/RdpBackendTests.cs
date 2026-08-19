using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMCreate;

namespace VMCreate.Tests.GalleryTests
{
    /// <summary>
    /// Tests for the <see cref="RdpBackend"/> enum, the <see cref="VmCustomizations.ConfigureXrdp"/>
    /// backward-compat shim, and <see cref="VmCustomizations.HasPreBootCustomizations"/> gating.
    /// </summary>
    [TestClass]
    public sealed class RdpBackendTests
    {
        [TestMethod]
        public void Default_RdpBackend_IsXrdp()
        {
            var c = new VmCustomizations();
            Assert.AreEqual(RdpBackend.Xrdp, c.RdpBackend);
        }

        [TestMethod]
        public void ConfigureXrdp_True_MapsToXrdp()
        {
            var c = new VmCustomizations { ConfigureXrdp = true };
            Assert.AreEqual(RdpBackend.Xrdp, c.RdpBackend);
            Assert.IsTrue(c.ConfigureXrdp);
        }

        [TestMethod]
        public void ConfigureXrdp_False_MapsToNone()
        {
            var c = new VmCustomizations { ConfigureXrdp = false };
            Assert.AreEqual(RdpBackend.None, c.RdpBackend);
            Assert.IsFalse(c.ConfigureXrdp);
        }

        [TestMethod]
        public void ConfigureXrdp_Shim_RoundTripsForXrdp()
        {
            var c = new VmCustomizations { RdpBackend = RdpBackend.Xrdp };
            Assert.IsTrue(c.ConfigureXrdp);
        }

        [TestMethod]
        public void ConfigureXrdp_Shim_ReturnsFalseForLamco()
        {
            var c = new VmCustomizations { RdpBackend = RdpBackend.Lamco };
            Assert.IsFalse(c.ConfigureXrdp);
        }

        [TestMethod]
        public void ConfigureXrdp_Shim_ReturnsFalseForNone()
        {
            var c = new VmCustomizations { RdpBackend = RdpBackend.None };
            Assert.IsFalse(c.ConfigureXrdp);
        }

        [TestMethod]
        public void HasPreBootCustomizations_TrueForXrdp()
        {
            var c = new VmCustomizations { RdpBackend = RdpBackend.Xrdp };
            Assert.IsTrue(c.HasPreBootCustomizations);
        }

        [TestMethod]
        public void HasPreBootCustomizations_FalseForLamco()
        {
            // Lamco installs post-boot over SSH — it does not trigger the cloning-ISO boot cycle.
            var c = new VmCustomizations { RdpBackend = RdpBackend.Lamco };
            Assert.IsFalse(c.HasPreBootCustomizations);
        }

        [TestMethod]
        public void HasPreBootCustomizations_FalseForNone()
        {
            var c = new VmCustomizations { RdpBackend = RdpBackend.None };
            Assert.IsFalse(c.HasPreBootCustomizations);
        }
    }
}