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
    /// Tests for <see cref="BlackArchInstallStep"/> — the engine behind the
    /// "BlackArch" gallery item (tag <c>blackarch</c>), delivered as an Arch
    /// base whose BlackArch repos are added and tools installed post-boot.
    /// Required (not user-togglable) — shipping plain Arch would be fabricated.
    /// </summary>
    [TestClass]
    public sealed class BlackArchInstallStepTests
    {
        private BlackArchInstallStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<BlackArchInstallStep>> _logger = null!;
        private GalleryItem _blackArchItem = null!;
        private GalleryItem _plainItem = null!;
        private VmCustomizations _customizations = null!;

        [TestInitialize]
        public void Setup()
        {
            _step = new BlackArchInstallStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<BlackArchInstallStep>>();
            _blackArchItem = new GalleryItem();
            _blackArchItem.Tags.Add("blackarch");
            _plainItem = new GalleryItem();
            _customizations = new VmCustomizations();
        }

        private void SetupScriptResult(string output)
            => _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(output);

        [TestMethod]
        public void StepMetadata_IsCorrect()
        {
            Assert.AreEqual("Add BlackArch Repositories and Tools", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(230, _step.Order);

            // ProgressPhaseId === DeployPhaseId (IDistributionOptionMetadata
            // pattern shared with KaliKdeSwitchStep).
            Assert.AreEqual("Sub_BlackArchInstall", _step.ProgressPhaseId);
            Assert.AreEqual(_step.DeployPhaseId, _step.ProgressPhaseId);
        }

        [TestMethod]
        public void DeployCardMetadata_IsCorrect()
        {
            Assert.AreEqual("BlackArch Repositories & Tools", _step.CardTitle);
            Assert.AreEqual("Install BlackArch tools (required)", _step.Label);
            Assert.AreEqual("Install BlackArch Tools", _step.DeployTitle);
            Assert.AreEqual("Shield24", _step.DeployIconName);
            Assert.AreEqual(230, _step.DeployOrder);
            Assert.IsFalse(_step.IsOptional, "the BlackArch conversion is required for the BlackArch item — shipping plain Arch would be fabricated");
            Assert.IsTrue(_step.DefaultEnabled);
            Assert.IsNull(_step.DeployCompletionInfo);
        }

        [TestMethod]
        public void IsApplicable_TagGated()
        {
            Assert.IsTrue(_step.IsApplicable(_blackArchItem, _customizations));
            Assert.IsFalse(_step.IsApplicable(_plainItem, _customizations), "plain Arch items never get the BlackArch conversion");
            Assert.IsTrue(_step.IsApplicable(_blackArchItem, null),
                "the conversion is required for the item — it cannot be disabled via customizations");
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Ok_Succeeds()
        {
            SetupScriptResult("installing blackarch-pentest...\nBLACKARCH_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _blackArchItem, _customizations, _logger.Object, CancellationToken.None);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Degraded_LogsWarning()
        {
            SetupScriptResult("WARN: failed to install burpsuite\nBLACKARCH_RESULT=degraded");

            await _step.ExecuteAsync(_shell.Object, _blackArchItem, _customizations, _logger.Object, CancellationToken.None);

            _logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("DEGRADED")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [TestMethod]
        public async Task ExecuteAsync_ResultLine_Missing_Throws()
        {
            // Zero exit with no BLACKARCH_RESULT line = fabricated success —
            // must throw.
            SetupScriptResult("some output but no terminal contract line");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _step.ExecuteAsync(_shell.Object, _blackArchItem, _customizations, _logger.Object, CancellationToken.None));
        }

        [TestMethod]
        public void ParseResultLine_LastOccurrenceWins()
        {
            // internal static — directly testable for the parser contract.
            Assert.AreEqual("ok", BlackArchInstallStep.ParseResultLine("BLACKARCH_RESULT=degraded\nBLACKARCH_RESULT=ok"));
            Assert.AreEqual("degraded", BlackArchInstallStep.ParseResultLine(" BLACKARCH_RESULT=degraded \n"));
            Assert.IsNull(BlackArchInstallStep.ParseResultLine("no contract line here"));
            Assert.IsNull(BlackArchInstallStep.ParseResultLine("BLACKARCH_RESULT=explosive"));
        }

        [TestMethod]
        public async Task ExecuteAsync_ShipsScriptWithBlackArchMarkersAndHardening()
        {
            string? copiedContent = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((content, path, _) => copiedContent = content)
                  .Returns(Task.CompletedTask);
            SetupScriptResult("BLACKARCH_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _blackArchItem, _customizations, _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.CopyContentAsync(
                It.IsAny<string>(),
                It.Is<string>(p => p.StartsWith("/tmp/blackarch_install_") && p.EndsWith(".sh")),
                It.IsAny<CancellationToken>()), Times.Once);
            _shell.Verify(s => s.RunCommandAsync(
                It.Is<string>(cmd => cmd.Contains("chown root:root") && cmd.Contains("chmod 0700") && cmd.Contains("rm -f")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);

            Assert.IsNotNull(copiedContent);
            StringAssert.Contains(copiedContent, "strap.sh", "bootstraps the BlackArch repository");
            StringAssert.Contains(copiedContent, "blackarch-pentest", "installs the core pentest group");
            StringAssert.Contains(copiedContent, "BLACKARCH_RESULT=", "carries the result-line contract");
            // The strap must be integrity-verified, not blindly executed.
            StringAssert.Contains(copiedContent, "sha1sum -c", "strap.sh is checksum-verified before execution");
        }
    }
}
