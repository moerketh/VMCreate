using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Pins that the xrdp/X11 fixup chain (orders 240–270) waits while an
    /// Auto backend still awaits a desktop switch. Live regression (Kali +
    /// GNOME): the chain ran in the ISO chroot under the unresolved Auto,
    /// force_x11.sh pre-seeded gdm3's WaylandEnable=false, the switched
    /// GNOME came up X11-only, and the resolver then picked xrdp instead
    /// of Lamco.
    /// </summary>
    [TestClass]
    public sealed class XrdpChainAutoDeferralTests
    {
        private static IEnumerable<object[]> ChainSteps()
        {
            yield return new object[] { new ForceX11Step() };
            yield return new object[] { new FixXrdpStep() };
            yield return new object[] { new DisableKwinCompositingStep() };
            yield return new object[] { new FixAccountsServiceStep() };
            yield return new object[] { new DisableWaylandSessionsStep() };
        }

        private static readonly GalleryItem KaliItem = new() { LinuxDistro = LinuxDistro.Kali };

        [TestMethod]
        [DynamicData(nameof(ChainSteps), DynamicDataSourceType.Method)]
        public void IsApplicable_WaitsWhileAutoAwaitsDesktopSwitch(ICustomizationStep step)
        {
            var pending = new VmCustomizations
            {
                RdpBackend = RdpBackend.Auto,
                DesktopEnvironmentId = "gnome"
            };
            Assert.IsFalse(step.IsApplicable(KaliItem, pending),
                $"{step.Name} must not run before the switched desktop resolves the backend");

            // DesktopSwitchStep clears the marker; if Auto then resolves to
            // xrdp the chain runs post-boot as usual.
            pending.DesktopEnvironmentId = null;
            pending.RdpBackend = RdpBackend.Xrdp;
            Assert.IsTrue(step.IsApplicable(KaliItem, pending),
                $"{step.Name} must run once Auto resolved to xrdp");
        }

        [TestMethod]
        [DynamicData(nameof(ChainSteps), DynamicDataSourceType.Method)]
        public void IsApplicable_ExplicitBackendsAndStockAuto_Unchanged(ICustomizationStep step)
        {
            // An explicit xrdp choice with a desktop switch is unaffected —
            // only the unresolved Auto waits.
            Assert.IsTrue(step.IsApplicable(KaliItem,
                new VmCustomizations { RdpBackend = RdpBackend.Xrdp, DesktopEnvironmentId = "kde" }));
            // Stock-desktop Auto keeps the historical chroot behaviour.
            Assert.IsTrue(step.IsApplicable(KaliItem, new VmCustomizations { RdpBackend = RdpBackend.Auto }));
            Assert.IsTrue(step.IsApplicable(KaliItem, null));
            Assert.IsFalse(step.IsApplicable(KaliItem,
                new VmCustomizations { RdpBackend = RdpBackend.Lamco, DesktopEnvironmentId = "kde" }));
            Assert.IsFalse(step.IsApplicable(KaliItem, new VmCustomizations { RdpBackend = RdpBackend.Wayrdp }));
        }
    }
}
