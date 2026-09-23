using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VMCreate.Tests.GUI
{
    /// <summary>
    /// Tests for the gallery visibility filter on
    /// <see cref="SelectImagePageViewModel"/>: the default view shows
    /// pre-installed images, recommended items, and ISO installers that ship
    /// an unattended install path (Windows unattend.xml, Omarchy cidata);
    /// manual-install ISOs stay hidden until "Show ISO installers" is ticked.
    /// The filter is intentionally not bypassed by text search — searching
    /// narrows the visible set, it never widens it.
    /// </summary>
    [TestClass]
    public sealed class SelectImagePageViewModelTests
    {
        private static GalleryItem PreInstalled(string name) => new()
        {
            Name = name,
            DiskUri = $"https://example.com/{name}.vhdx",
        };

        private static GalleryItem ManualIso(string name) => new()
        {
            Name = name,
            DiskUri = $"https://example.com/{name}.iso",
        };

        private static GalleryItem UnattendedIso(string name) => new()
        {
            Name = name,
            DiskUri = $"https://example.com/{name}.iso",
            HasUnattendedInstallPath = true,
        };

        private static SelectImagePageViewModel CreateViewModel(params GalleryItem[] items)
        {
            var wizardData = new WizardData
            {
                GalleryItems = new ObservableCollection<GalleryItem>(items),
            };
            return new SelectImagePageViewModel(wizardData, new Mock<ILogger>().Object);
        }

        private static List<string> VisibleNames(SelectImagePageViewModel vm) =>
            vm.GalleryView.Cast<GalleryItem>().Select(i => i.Name).ToList();

        [TestMethod]
        public void DefaultFilter_HidesManualIsoInstallers()
        {
            var vm = CreateViewModel(ManualIso("Athena OS (Arch)"), ManualIso("Kali Linux"));

            Assert.AreEqual(0, VisibleNames(vm).Count);
        }

        [TestMethod]
        public void DefaultFilter_ShowsUnattendedIsoInstallers()
        {
            // Omarchy is ISO media but deploys unattended via its cidata seed —
            // it must not be gated behind the toggle.
            var vm = CreateViewModel(UnattendedIso("Omarchy 4.0.4"));

            CollectionAssert.AreEquivalent(new[] { "Omarchy 4.0.4" }, VisibleNames(vm));
        }

        [TestMethod]
        public void DefaultFilter_ShowsPreInstalledAndRecommendedItems()
        {
            var recommendedIso = ManualIso("FLARE VM");
            recommendedIso.IsRecommended = true;

            var vm = CreateViewModel(PreInstalled("Ubuntu 24.04"), recommendedIso);

            CollectionAssert.AreEquivalent(
                new[] { "Ubuntu 24.04", "FLARE VM" }, VisibleNames(vm));
        }

        [TestMethod]
        public void ShowIsoInstallers_RevealsManualIsoInstallers()
        {
            var vm = CreateViewModel(ManualIso("Athena OS (Arch)"), UnattendedIso("Omarchy 4.0.4"));

            vm.ShowIsoInstallers = true;

            CollectionAssert.AreEquivalent(
                new[] { "Athena OS (Arch)", "Omarchy 4.0.4" }, VisibleNames(vm));
        }

        [TestMethod]
        public void ShowIsoInstallers_ToggledBackOff_HidesManualIsoInstallersAgain()
        {
            var vm = CreateViewModel(ManualIso("Athena OS (Arch)"), UnattendedIso("Omarchy 4.0.4"));

            vm.ShowIsoInstallers = true;
            vm.ShowIsoInstallers = false;

            CollectionAssert.AreEquivalent(new[] { "Omarchy 4.0.4" }, VisibleNames(vm));
        }

        [TestMethod]
        public void SearchText_NarrowsButNeverWidensTheVisibleSet()
        {
            // Text search matches name/publisher/description, but the ISO-toggle
            // gate applies first: searching for a hidden manual ISO shows nothing.
            var vm = CreateViewModel(
                ManualIso("Athena OS (Arch)"),
                UnattendedIso("Omarchy 4.0.4"),
                PreInstalled("Ubuntu 24.04"));

            vm.SearchText = "athena";
            Assert.AreEqual(0, VisibleNames(vm).Count);

            vm.SearchText = "4"; // matches "Omarchy 4.0.4" and "Ubuntu 24.04"
            CollectionAssert.AreEquivalent(
                new[] { "Omarchy 4.0.4", "Ubuntu 24.04" }, VisibleNames(vm));
        }
    }
}
