using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Tests for <see cref="KaliKdeSwitchStep"/> — the engine behind the
    /// "Kali … (KDE)" gallery twins (tag <c>kali-kde</c>). Required (not
    /// user-togglable), tag-gated, order 231 BEFORE the Auto RDP resolver
    /// (232) so the resolver sees the post-switch Wayland-default Plasma
    /// session.
    /// </summary>
    [TestClass]
    public sealed class KaliKdeSwitchStepTests
    {
        private KaliKdeSwitchStep _step;
        private Mock<IGuestShell> _shell;
        private Mock<ILogger<KaliKdeSwitchStep>> _logger;
        private GalleryItem _kdeItem;
        private GalleryItem _plainItem;
        private VmCustomizations _autoCustomizations;

        [TestInitialize]
        public void Setup()
        {
            _step = new KaliKdeSwitchStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<KaliKdeSwitchStep>>();
            _kdeItem = new GalleryItem { LinuxDistro = LinuxDistro.Kali };
            _kdeItem.Tags.Add("kali-kde");
            _plainItem = new GalleryItem { LinuxDistro = LinuxDistro.Kali };
            _autoCustomizations = new VmCustomizations();
        }

        private void SetupScriptResult(string output)
            => _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(output);

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Switch Kali to KDE Plasma", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(231, _step.Order);

            // ProgressPhaseId === DeployPhaseId (IDistributionOptionMetadata
            // pattern shared with FlareVmStabilizationStep).
            Assert.AreEqual("Sub_KaliKdeSwitch", _step.ProgressPhaseId);
            Assert.AreEqual(_step.DeployPhaseId, _step.ProgressPhaseId);
        }

        [TestMethod]
        public void DeployCardMetadata_IsCorrect()
        {
            Assert.AreEqual("KDE Plasma Desktop", _step.CardTitle);
            Assert.AreEqual("Switch to KDE Plasma (required)", _step.Label);
            Assert.AreEqual("Switch to KDE Plasma", _step.DeployTitle);
            Assert.AreEqual("Desktop24", _step.DeployIconName);
            Assert.AreEqual(231, _step.DeployOrder);
            Assert.IsFalse(_step.IsOptional, "the KDE switch is required for KDE items — shipping XFCE would be fabricated");
            Assert.IsTrue(_step.DefaultEnabled);
            Assert.IsNull(_step.DeployCompletionInfo);
        }

        [TestMethod]
        public void IsApplicable_TagGated_IndependentOfRdpBackend()
        {
            // The gate is the kali-kde tag alone — the desktop switch runs
            // under every backend choice (Auto/Xrdp/Lamco/None).
            Assert.IsTrue(_step.IsApplicable(_kdeItem, _autoCustomizations));
            Assert.IsTrue(_step.IsApplicable(_kdeItem, new VmCustomizations { RdpBackend = RdpBackend.Xrdp }));
            Assert.IsTrue(_step.IsApplicable(_kdeItem, new VmCustomizations { RdpBackend = RdpBackend.Lamco }));

            Assert.IsFalse(_step.IsApplicable(_plainItem, _autoCustomizations), "plain Kali items never switch desktops");

            // The gate reads ONLY the item tag: the customizations instance is
            // irrelevant (and never null in practice — the service supplies
            // the live instance).
            Assert.IsTrue(_step.IsApplicable(_kdeItem, null),
                "the desktop switch is required for the item — it cannot be disabled via customizations");
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Ok_Succeeds()
        {
            SetupScriptResult("Setting up kali-desktop-kde...\nKALI_KDE_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _kdeItem, _autoCustomizations, _logger.Object, CancellationToken.None);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Degraded_LogsWarning()
        {
            // "degraded": the desktop itself switched; a side task (e.g. the
            // session-manager alternative) failed — warn, don't fail.
            SetupScriptResult("update-alternatives failed\nKALI_KDE_RESULT=degraded");

            await _step.ExecuteAsync(_shell.Object, _kdeItem, _autoCustomizations, _logger.Object, CancellationToken.None);

            _logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("DEGRADED")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Missing_Throws()
        {
            // Zero exit with no KALI_KDE_RESULT line = fabricated success —
            // must throw.
            SetupScriptResult("some output but no terminal contract line");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _kdeItem, _autoCustomizations, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        public void ParseResultLine_LastOccurrenceWins()
        {
            // internal static — directly testable for the parser contract.
            Assert.AreEqual("ok", KaliKdeSwitchStep.ParseResultLine("KALI_KDE_RESULT=degraded\nKALI_KDE_RESULT=ok"));
            Assert.AreEqual("degraded", KaliKdeSwitchStep.ParseResultLine(" KALI_KDE_RESULT=degraded \n"));
            Assert.IsNull(KaliKdeSwitchStep.ParseResultLine("no contract line here"));
            Assert.IsNull(KaliKdeSwitchStep.ParseResultLine("KALI_KDE_RESULT=explosive"));
        }

        [TestMethod]
        public async Task ExecuteAsync_ShipsScriptWithKdeMarkersAndHardening()
        {
            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, path, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            SetupScriptResult("KALI_KDE_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _kdeItem, _autoCustomizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.IsAny<string>(),
                It.Is<string>(p => p.StartsWith("/tmp/kali_kde_switch_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);
            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("chown root:root") && cmd.Contains("chmod 0700") && cmd.Contains("rm -f")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);

            Assert.IsNotNull(copiedContent);
            StringAssert.Contains(copiedContent, "kali-desktop-kde", "installs the KDE desktop metapackage");
            StringAssert.Contains(copiedContent, "kali-desktop-xfce", "purges the stock XFCE desktop");
            StringAssert.Contains(copiedContent, "KALI_KDE_RESULT=", "carries the result-line contract");
        }
    }
}