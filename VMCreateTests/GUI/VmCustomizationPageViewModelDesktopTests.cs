using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using VMCreate.Gallery;

namespace VMCreate.Tests.GUI
{
    /// <summary>
    /// Desktop-card coupling tests for <see cref="VmCustomizationPageViewModel"/>:
    /// desktop selection must drive Lamco availability (an X11-only desktop
    /// like Xfce can never be served by the Wayland-native Lamco RDP Server),
    /// reset now-impossible explicit Lamco choices to Auto, and normalize the
    /// finished wizard data so the stock desktop means "no switch".
    /// </summary>
    [TestClass]
    public sealed class VmCustomizationPageViewModelDesktopTests
    {
        private static VmCustomizationPageViewModel Create(
            GalleryItem? item, out WizardData wizardData)
        {
            wizardData = new WizardData { SelectedItem = item };
            var htbApi = new Mock<IHtbApiClient>();
            htbApi.Setup(a => a.EndpointNames).Returns(Array.Empty<string>());
            return new VmCustomizationPageViewModel(
                wizardData, htbApi.Object, NullLogger.Instance,
                configurableSteps: Enumerable.Empty<IConfigurableCustomizationStep>(),
                desktopCatalogs: new[] { new KaliDesktopCatalog() });
        }

        private static GalleryItem KaliItem() =>
            new() { Name = "Kali Weekly", LinuxDistro = LinuxDistro.Kali, IsWindows = false };

        [TestMethod]
        public void KaliItem_ShowsDesktopCardWithThreeOptions()
        {
            var vm = Create(KaliItem(), out _);

            Assert.IsTrue(vm.ShowDesktopOptions);
            Assert.AreEqual(3, vm.DesktopOptions.Count);
            Assert.AreEqual("Xfce", vm.DefaultDesktopName);
            Assert.IsTrue(vm.IsKeepDefaultDesktop, "no desktop is pre-selected — stock desktop is the default choice");
        }

        [TestMethod]
        public void NonCatalogDistro_HidesDesktopCard()
        {
            var vm = Create(new GalleryItem { Name = "Fedora", LinuxDistro = LinuxDistro.Unknown }, out _);

            Assert.IsFalse(vm.ShowDesktopOptions);
            Assert.AreEqual(0, vm.DesktopOptions.Count);
        }

        [TestMethod]
        public void DefaultAndXfce_DisableLamco_KdeAndGnome_EnableIt()
        {
            var vm = Create(KaliItem(), out _);

            // Stock Xfce (default, nothing picked) is X11-only.
            Assert.IsFalse(vm.LamcoOptionEnabled, "stock Xfce cannot use the Wayland-native Lamco server");
            Assert.IsTrue(vm.ShowAutoRdpHint, "Auto needs the X11-only explanation on Kali");
            StringAssert.Contains(vm.AutoRdpHint, "xrdp");

            vm.SelectDesktop("kde");
            Assert.IsTrue(vm.LamcoOptionEnabled, "Plasma 6 (Wayland) is Lamco-capable");
            Assert.IsFalse(vm.IsKeepDefaultDesktop);

            vm.SelectDesktop("xfce");
            Assert.IsFalse(vm.LamcoOptionEnabled, "explicitly picking Xfce is still X11-only");

            vm.SelectDesktop("gnome");
            Assert.IsTrue(vm.LamcoOptionEnabled, "GNOME (Wayland) is Lamco-capable");
        }

        [TestMethod]
        public void SelectingX11OnlyDesktop_ResetsExplicitLamcoToAuto()
        {
            var vm = Create(KaliItem(), out _);

            // Get into a Lamco-legal state first…
            vm.SelectDesktop("kde");
            vm.SelectedRdpBackend = RdpBackend.Lamco;
            Assert.IsTrue(vm.IsRdpBackendLamco);

            // …then pick a desktop that Lamco cannot serve.
            vm.SelectDesktop("xfce");

            Assert.IsFalse(vm.LamcoOptionEnabled);
            Assert.IsTrue(vm.IsRdpBackendAuto,
                "explicit Lamco must be reset to Auto instead of failing at deploy time");
        }

        [TestMethod]
        public void Finish_KeepDefault_NormalizesDesktopIdToNull()
        {
            var vm = Create(KaliItem(), out var wizardData);

            // Selecting the stock desktop explicitly is the same as keeping it.
            vm.SelectDesktop("xfce");
            vm.FinishCommand.Execute(null);

            Assert.IsNull(wizardData.Customizations.DesktopEnvironmentId,
                "no DesktopSwitchStep should run for the stock desktop");
        }

        [TestMethod]
        public void Finish_KdeSelection_PersistsDesktopId()
        {
            var vm = Create(KaliItem(), out var wizardData);

            vm.SelectDesktop("kde");
            vm.FinishCommand.Execute(null);

            Assert.AreEqual("kde", wizardData.Customizations.DesktopEnvironmentId);
        }

        [TestMethod]
        public void Finish_DefensiveLamcoReset_ForProgrammaticallyBrokenState()
        {
            var vm = Create(KaliItem(), out var wizardData);

            vm.SelectDesktop("kde");
            vm.SelectedRdpBackend = RdpBackend.Lamco;
            // Bypass the coupling by switching desktops through the raw
            // property paths the UI can hit when bindings update out of
            // order: SelectedRdpBackend is left on Lamco while the desktop
            // becomes X11-only.
            vm.SelectDesktop("xfce");
            vm.SelectedRdpBackend = RdpBackend.Lamco; // stale binding write after the reset

            vm.FinishCommand.Execute(null);

            Assert.AreEqual(RdpBackend.Auto, wizardData.Customizations.RdpBackend,
                "OnFinish must not ship a Lamco selection the desktop cannot serve");
            Assert.IsNull(wizardData.Customizations.DesktopEnvironmentId);
        }

        [TestMethod]
        public void SelectDesktop_UpdatesRadioSelectionFlags()
        {
            var vm = Create(KaliItem(), out _);

            vm.SelectDesktop("gnome");

            Assert.IsTrue(vm.DesktopOptions.Single(o => o.Id == "gnome").IsSelected);
            Assert.IsFalse(vm.DesktopOptions.Single(o => o.Id == "kde").IsSelected);
            Assert.IsFalse(vm.DesktopOptions.Single(o => o.Id == "xfce").IsSelected);
            Assert.IsFalse(vm.IsKeepDefaultDesktop);

            vm.SelectDesktop(null);
            Assert.IsTrue(vm.IsKeepDefaultDesktop);
            Assert.IsFalse(vm.DesktopOptions.Single(o => o.Id == "gnome").IsSelected);
        }
    }
}
