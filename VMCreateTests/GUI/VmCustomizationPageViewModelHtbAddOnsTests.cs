using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Linq;

namespace VMCreate.Tests.GUI
{
    /// <summary>
    /// HTB VPN has no card on the customization page: it is configured when
    /// an HTB App Token is stored in Settings and the image is a Linux
    /// security distribution. Optional steps are grouped in one Add-Ons card,
    /// hidden when the image has none.
    /// </summary>
    [TestClass]
    public sealed class VmCustomizationPageViewModelHtbAddOnsTests
    {
        private static VmCustomizationPageViewModel Create(
            GalleryItem item, string? storedToken, out WizardData wizardData,
            params IConfigurableCustomizationStep[] steps)
        {
            wizardData = new WizardData { SelectedItem = item };
            wizardData.Preferences.HtbApiToken = storedToken;
            return new VmCustomizationPageViewModel(wizardData, NullLogger.Instance, steps);
        }

        private static GalleryItem SecurityLinux() => new() { Name = "Kali", Category = "Security", IsWindows = false };

        [TestMethod]
        public void Finish_WithStoredToken_OnLinuxSecurityImage_ConfiguresHtbVpn()
        {
            var vm = Create(SecurityLinux(), "token", out var data);

            vm.FinishCommand.Execute(null);

            Assert.IsTrue(data.Customizations.ConfigureHtbVpn);
            Assert.AreEqual(0, data.Customizations.HtbVpnKeys.Count, "the deploy step downloads the keys");
        }

        [TestMethod]
        public void Finish_WithoutStoredToken_DoesNotConfigureHtbVpn()
        {
            var vm = Create(SecurityLinux(), "  ", out var data);

            vm.FinishCommand.Execute(null);

            Assert.IsFalse(data.Customizations.ConfigureHtbVpn);
        }

        [TestMethod]
        public void Finish_WithStoredToken_OnWindowsSecurityImage_DoesNotConfigureHtbVpn()
        {
            var vm = Create(new GalleryItem { Name = "FLARE VM", Category = "Security", IsWindows = true }, "token", out var data);

            vm.FinishCommand.Execute(null);

            Assert.IsFalse(data.Customizations.ConfigureHtbVpn);
        }

        private static IConfigurableCustomizationStep OptionalStep(string name, bool optional = true)
        {
            var step = new Mock<IConfigurableCustomizationStep>();
            step.Setup(s => s.Name).Returns(name);
            step.Setup(s => s.CardTitle).Returns(name);
            step.Setup(s => s.CardDescription).Returns("");
            step.Setup(s => s.Label).Returns(name);
            step.Setup(s => s.Tooltip).Returns("");
            step.Setup(s => s.IsOptional).Returns(optional);
            step.Setup(s => s.IsVisibleFor(It.IsAny<GalleryItem>())).Returns(true);
            return step.Object;
        }

        [TestMethod]
        public void AddOns_ListsOnlyOptionalSteps_AndFinishRecordsTheChecked()
        {
            var vm = Create(SecurityLinux(), null, out var data,
                OptionalStep("Elastic SIEM"), OptionalStep("Caido"), OptionalStep("Required", optional: false));

            Assert.IsTrue(vm.HasDistributionOptions);
            CollectionAssert.AreEqual(new[] { "Elastic SIEM", "Caido" }, vm.DistributionOptions.Select(o => o.Name).ToArray());

            vm.DistributionOptions[0].IsEnabled = true;
            vm.DistributionOptions[1].IsEnabled = true;
            vm.FinishCommand.Execute(null);

            CollectionAssert.AreEquivalent(new[] { "Elastic SIEM", "Caido" },
                data.Customizations.DistributionOptions.Select(o => o.Name).ToArray());
        }

        [TestMethod]
        public void AddOns_HiddenWhenImageHasNone()
        {
            var vm = Create(SecurityLinux(), null, out _);

            Assert.IsFalse(vm.HasDistributionOptions);
        }
    }
}
