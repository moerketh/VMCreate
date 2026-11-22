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
    /// finished wizard data so the stock desktop means "no switch". Also pins
    /// the xrdp coupling (X11 desktops only; a KDE/GNOME pick resets xrdp to
    /// Auto with a notice) and the wayrdp radio (Omarchy images only).
    /// </summary>
    [TestClass]
    public sealed class VmCustomizationPageViewModelDesktopTests
    {
        private static VmCustomizationPageViewModel Create(
            GalleryItem? item, out WizardData wizardData, RdpBackend storedBackend = RdpBackend.Auto)
        {
            wizardData = new WizardData { SelectedItem = item };
            wizardData.Customizations.RdpBackend = storedBackend;
            return new VmCustomizationPageViewModel(
                wizardData, NullLogger.Instance,
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

        // ── wayrdp radio visibility ────────────────────────────────────
        // wayrdp only serves Hyprland today, so the radio renders for
        // Omarchy images only.

        [TestMethod]
        public void WayrdpRadio_OnKali_EnabledForKdeAndGnome()
        {
            var vm = Create(KaliItem(), out _);

            Assert.IsTrue(vm.WayrdpOptionVisible, "Kali is a wayrdp KDE- and GNOME-backend target");
            Assert.IsFalse(vm.WayrdpOptionEnabled, "stock Xfce: wayrdp has no Xfce backend");

            vm.SelectDesktop("kde");
            Assert.IsTrue(vm.WayrdpOptionEnabled);
            StringAssert.Contains(vm.AutoRdpHint, "wayrdp");
            StringAssert.Contains(vm.AutoRdpHint, "KDE Plasma");

            vm.SelectDesktop("gnome");
            Assert.IsTrue(vm.WayrdpOptionEnabled, "wayrdp's GNOME backend");
            StringAssert.Contains(vm.AutoRdpHint, "GNOME");
            StringAssert.Contains(vm.AutoRdpHint, "wayrdp");
        }

        [TestMethod]
        public void WayrdpChoice_ResetsToAuto_WhenKdeIsDeselected()
        {
            var vm = Create(KaliItem(), out _);
            vm.SelectDesktop("kde");
            vm.IsRdpBackendWayrdp = true;
            Assert.AreEqual(RdpBackend.Wayrdp, vm.SelectedRdpBackend);

            vm.SelectDesktop(null);

            Assert.AreEqual(RdpBackend.Auto, vm.SelectedRdpBackend);
            Assert.IsTrue(vm.HasRdpBackendNotice);
        }

        [TestMethod]
        public void WayrdpRadio_VisibleOnOmarchy()
        {
            var vm = Create(new GalleryItem { Name = "Omarchy", LinuxDistro = LinuxDistro.Arch, Tags = new List<string> { "omarchy" } }, out _);

            Assert.IsTrue(vm.WayrdpOptionVisible);
        }

        [TestMethod]
        public void StoredWayrdpPreference_OnKali_ResetsToAutoWithNotice()
        {
            var vm = Create(KaliItem(), out _, RdpBackend.Wayrdp);

            Assert.AreEqual(RdpBackend.Auto, vm.SelectedRdpBackend);
            Assert.IsTrue(vm.HasRdpBackendNotice);
        }

        // ── xrdp only on X11 desktops ──────────────────────────────────

        [TestMethod]
        public void XrdpRadio_EnabledForStockXfce()
        {
            var vm = Create(KaliItem(), out _, RdpBackend.Xrdp);

            Assert.IsTrue(vm.XrdpOptionEnabled);
            Assert.AreEqual(RdpBackend.Xrdp, vm.SelectedRdpBackend, "Xfce is X11 — xrdp stays");
            Assert.IsFalse(vm.HasRdpBackendNotice);
        }

        [TestMethod]
        public void PickingGnome_ResetsXrdpToAutoWithNotice()
        {
            var vm = Create(KaliItem(), out _, RdpBackend.Xrdp);
            var changed = new List<string?>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            vm.SelectDesktop("gnome");

            Assert.IsFalse(vm.XrdpOptionEnabled);
            Assert.AreEqual(RdpBackend.Auto, vm.SelectedRdpBackend);
            Assert.IsTrue(vm.HasRdpBackendNotice);
            StringAssert.Contains(vm.RdpBackendNotice, "GNOME");
            CollectionAssert.Contains(changed, nameof(vm.IsRdpBackendAuto), "the Auto radio must re-check");
            CollectionAssert.Contains(changed, nameof(vm.IsRdpBackendXrdp), "the xrdp radio must un-check");
        }

        [TestMethod]
        public void PickingKde_DisablesXrdp_BackToXfceEnablesIt()
        {
            var vm = Create(KaliItem(), out _);

            vm.SelectDesktop("kde");
            Assert.IsFalse(vm.XrdpOptionEnabled);

            vm.SelectDesktop(null);
            Assert.IsTrue(vm.XrdpOptionEnabled);
        }

        [TestMethod]
        public void ExplicitPick_ClearsNotice()
        {
            var vm = Create(KaliItem(), out _, RdpBackend.Xrdp);
            vm.SelectDesktop("gnome");
            Assert.IsTrue(vm.HasRdpBackendNotice);

            vm.IsRdpBackendLamco = true;

            Assert.IsFalse(vm.HasRdpBackendNotice);
        }

        [TestMethod]
        public void XrdpRadio_AlwaysEnabledWithoutCatalog()
        {
            // Parrot has no desktop catalog: xrdp keeps its Force X11 fallback.
            var vm = Create(new GalleryItem { Name = "Parrot", LinuxDistro = LinuxDistro.Parrot }, out _, RdpBackend.Xrdp);

            Assert.IsTrue(vm.XrdpOptionEnabled);
            Assert.AreEqual(RdpBackend.Xrdp, vm.SelectedRdpBackend);
        }

        [TestMethod]
        public void WayrdpRadio_HiddenOnUnsupportedDistro()
        {
            var vm = Create(new GalleryItem { Name = "Fedora", LinuxDistro = LinuxDistro.Unknown }, out _);

            Assert.IsFalse(vm.WayrdpOptionVisible);
        }
    }
}
