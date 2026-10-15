using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Tests for <see cref="ConsoleResolutionValidator"/> — the gate for the
    /// machine-level console resolution before it is substituted into
    /// root-run guest scripts (Hyprland Lua, GNOME monitors.xml, KDE
    /// kscreen-doctor, plus log echoes). The value can be hand-edited into
    /// settings.json, so a hostile shape must fail on the host.
    /// </summary>
    [TestClass]
    public sealed class ConsoleResolutionValidatorTests
    {
        [TestMethod]
        // The Settings-window presets — every offered value must be valid,
        // plus sane hand-edits (shape-only validation deliberately does not
        // police which modes the guest supports: mode availability is the
        // synthetic display's business and unsupported modes degrade there).
        [DataRow("1280x720@60")]
        [DataRow("1360x768@60")]
        [DataRow("1600x900@60")]
        [DataRow("1920x1080@60")]
        [DataRow("1920x1200@60")]
        [DataRow("2560x1440@144")]
        [DataRow("3840x2160@60")]
        public void IsValidConsoleResolution_AcceptsShapeSafeValues(string resolution)
        {
            Assert.IsTrue(ConsoleResolutionValidator.IsValidConsoleResolution(resolution),
                $"'{resolution}' should be valid");
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("1920x1080")]             // missing rate
        [DataRow("1920")]                 // not a WxH@R triple
        [DataRow("1920X1080@60")]          // uppercase X
        [DataRow("99x99@60")]              // two-digit dimension
        [DataRow("1920x108000@60")]        // six-digit height
        [DataRow("1920x1080@6")]           // one-digit rate (below 10 Hz)
        [DataRow("1920x1080@1000")]        // four-digit rate
        [DataRow("0100x720@60")]           // leading-zero width
        [DataRow("100x0720@60")]           // leading-zero height
        [DataRow("1920x1080@060")]          // leading-zero rate
        [DataRow(" 1920x1080@60")]         // leading space
        [DataRow("1920x1080@60 ")]         // trailing space
        [DataRow("1920x1080@60;rm -rf /")] // injection
        [DataRow("1920x1080@60 && echo pwned")]
        [DataRow("$(reboot)x1080@60")]
        [DataRow("1920x{0}x1080@60")]       // braces
        [DataRow("1920x1080\n@60")]        // embedded newline
        [DataRow("1920x1080@60extra")]
        [DataRow("1920/x1080@60")]
        [DataRow("'')x1080@60")]
        public void IsValidConsoleResolution_RejectsHostileOrMalformedValues(string resolution)
        {
            Assert.IsFalse(ConsoleResolutionValidator.IsValidConsoleResolution(resolution),
                $"'{resolution}' must be rejected");
        }

        [TestMethod]
        public void TryGetTokenValues_MapsAllFourDerivedForms()
        {
            Assert.IsTrue(ConsoleResolutionValidator.TryGetTokenValues("1600x900@75",
                out var value, out var width, out var height, out var rateXml));

            Assert.AreEqual("1600x900@75", value);
            Assert.AreEqual("1600", width);
            Assert.AreEqual("900", height);
            // GNOME monitors.xml wants the three-decimal rate.
            Assert.AreEqual("75.000", rateXml);
        }

        [TestMethod]
        public void TryGetTokenValues_DefaultMode_MapsTo60Point000()
        {
            Assert.IsTrue(ConsoleResolutionValidator.TryGetTokenValues(ConsoleResolutionValidator.Default,
                out var value, out var width, out var height, out var rateXml));

            Assert.AreEqual("1920x1080@60", value);
            Assert.AreEqual("1920", width);
            Assert.AreEqual("1080", height);
            Assert.AreEqual("60.000", rateXml);
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("1600x900")]
        [DataRow("16x9@60")]
        [DataRow("1600x900@6")]
        [DataRow("1600x900@60;rm -rf /")]
        public void TryGetTokenValues_InvalidInput_ReturnsFalseWithEmptyValues(string resolution)
        {
            Assert.IsFalse(ConsoleResolutionValidator.TryGetTokenValues(resolution,
                out var value, out var width, out var height, out var rateXml));
            Assert.AreEqual(string.Empty, value);
            Assert.AreEqual(string.Empty, width);
            Assert.AreEqual(string.Empty, height);
            Assert.AreEqual(string.Empty, rateXml);
        }

        [TestMethod]
        public void TryGetTokenValues_DerivedStrings_AreInvariantFormatted()
        {
            // Token values are re-formatted from the parsed integers with an
            // invariant culture — the raw input never flows into the scripts,
            // so whatever a hand-edit smuggles past the shape anchor cannot
            // survive into a root-run script context.
            Assert.IsTrue(ConsoleResolutionValidator.TryGetTokenValues("1600x900@75",
                out var value, out _, out _, out var rateXml));

            Assert.IsFalse(value.Contains(";"));
            Assert.IsFalse(value.Contains(" "));
            Assert.IsFalse(rateXml.Contains(","));
            Assert.AreEqual("1600x900@75", value);
            Assert.AreEqual("75.000", rateXml);
        }
    }
}