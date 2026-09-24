using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using VMCreate.Gallery;

namespace VMCreate.Tests.GUI
{
    /// <summary>
    /// Release-picker tests for <see cref="VmSettingsPageViewModel"/>: the
    /// release card appears only for images with multiple releases, defaults
    /// to the default variant without requiring a choice, persists the
    /// selection (normalizing the default to null), and never touches the
    /// user's VM name — the name derives from the variant-invariant item
    /// name.
    /// </summary>
    [TestClass]
    public sealed class VmSettingsPageViewModelReleaseTests
    {
        private static VmSettingsPageViewModel Create(GalleryItem? item, WizardData? wizardData = null)
        {
            wizardData ??= new WizardData { SelectedItem = item };
            return new VmSettingsPageViewModel(wizardData, NullLogger.Instance);
        }

        private static VmSettingsPageViewModel Create(GalleryItem? item, out WizardData wizardData)
        {
            wizardData = new WizardData { SelectedItem = item };
            return new VmSettingsPageViewModel(wizardData, NullLogger.Instance);
        }

        private static GalleryItem MultiReleaseKali() => new()
        {
            Name = "Kali Linux",
            DiskUri = "https://example.com/kali-2026.2.qcow2",
            Version = "2026.2",
            LinuxDistro = LinuxDistro.Kali,
            ReleaseVariants = new List<ReleaseVariant>
            {
                new()
                {
                    Id = "stable",
                    DisplayName = "Stable (2026.2)",
                    DiskUri = "https://example.com/kali-2026.2.qcow2",
                    Version = "2026.2",
                    IsDefault = true,
                },
                new()
                {
                    Id = "weekly",
                    DisplayName = "Weekly (2026-W38)",
                    DiskUri = "https://example.com/kali-weekly-W38.qcow2",
                    Version = "2026-W38",
                    DownloadSizeText = "3.7G",
                },
            },
        };

        private static GalleryItem SingleReleaseItem() => new()
        {
            Name = "Ubuntu",
            DiskUri = "https://example.com/u.qcow2",
            Version = "24.04",
        };

        [TestMethod]
        public void MultiReleaseItem_ShowsReleaseCardWithAllVariants()
        {
            var vm = Create(MultiReleaseKali());

            Assert.IsTrue(vm.ShowReleaseOptions);
            Assert.AreEqual(2, vm.ReleaseOptions.Count);
            CollectionAssert.AreEquivalent(
                new[] { "stable", "weekly" },
                vm.ReleaseOptions.Select(o => o.Id).ToList());
        }

        [TestMethod]
        public void SingleReleaseItem_HidesReleaseCard()
        {
            var vm = Create(SingleReleaseItem());

            Assert.IsFalse(vm.ShowReleaseOptions);
            Assert.AreEqual(0, vm.ReleaseOptions.Count);
        }

        [TestMethod]
        public void NullSelectedItem_HidesReleaseCard()
        {
            var vm = Create(null);

            Assert.IsFalse(vm.ShowReleaseOptions);
        }

        [TestMethod]
        public void DefaultVariant_IsPreSelected_WithoutExplicitChoice()
        {
            var vm = Create(MultiReleaseKali());

            Assert.AreEqual("Kali Linux", vm.VmName,
                "the VM name derives from the variant-invariant item name");
            Assert.IsTrue(vm.ReleaseOptions.Single(o => o.Id == "stable").IsSelected);
            Assert.IsFalse(vm.ReleaseOptions.Single(o => o.Id == "weekly").IsSelected);
        }

        [TestMethod]
        public void SelectRelease_UpdatesRadioFlags()
        {
            var vm = Create(MultiReleaseKali());

            vm.SelectReleaseCommand.Execute("weekly");

            Assert.IsTrue(vm.ReleaseOptions.Single(o => o.Id == "weekly").IsSelected);
            Assert.IsFalse(vm.ReleaseOptions.Single(o => o.Id == "stable").IsSelected);
        }

        [TestMethod]
        public void Next_WithDefaultSelection_PersistsNullReleaseId()
        {
            var vm = Create(MultiReleaseKali(), out var wizardData);

            vm.NextCommand.Execute(null);

            Assert.IsNull(wizardData.SelectedReleaseId,
                "the default variant means the item itself — no release id needed");
        }

        [TestMethod]
        public void Next_WithWeeklySelection_PersistsReleaseId()
        {
            var vm = Create(MultiReleaseKali(), out var wizardData);

            vm.SelectReleaseCommand.Execute("weekly");
            vm.NextCommand.Execute(null);

            Assert.AreEqual("weekly", wizardData.SelectedReleaseId);
        }

        [TestMethod]
        public void Next_WithExplicitDefaultSelection_NormalizesBackToNull()
        {
            var vm = Create(MultiReleaseKali(), out var wizardData);

            // Re-selecting stable explicitly must not record "stable" —
            // the wizard data always describes the item itself for default.
            vm.SelectReleaseCommand.Execute("stable");
            vm.NextCommand.Execute(null);

            Assert.IsNull(wizardData.SelectedReleaseId);
        }

        [TestMethod]
        public void Next_PreservesUserTypedVmName_DespiteReleaseSwitch()
        {
            var vm = Create(MultiReleaseKali(), out var wizardData);

            vm.VmName = "my-pentest-box";
            vm.SelectReleaseCommand.Execute("weekly");
            vm.NextCommand.Execute(null);

            Assert.AreEqual("my-pentest-box", wizardData.Settings.VMName,
                "switching releases must never re-default or rename the VM");
            Assert.AreEqual("weekly", wizardData.SelectedReleaseId);
        }

        [TestMethod]
        public void RevisitedPage_RestoresPreviousWeeklySelection()
        {
            var item = MultiReleaseKali();
            var wizardData = new WizardData
            {
                SelectedItem = item,
                SelectedReleaseId = "weekly",
            };

            var vm = new VmSettingsPageViewModel(wizardData, NullLogger.Instance);

            Assert.IsTrue(vm.ReleaseOptions.Single(o => o.Id == "weekly").IsSelected,
                "back-navigation must restore the earlier release choice");
        }
    }
}
