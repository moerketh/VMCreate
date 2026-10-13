using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>Tests for <see cref="VulnHunterInstallStep"/> — optional, omarchy-gated.</summary>
    [TestClass]
    public sealed class VulnHunterInstallStepTests
    {
        private VulnHunterInstallStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<VulnHunterInstallStep>> _logger = null!;
        private GalleryItem _item = null!;
        private VmCustomizations _enabled = null!;
        private VmCustomizations _none = null!;

        [TestInitialize]
        public void Setup()
        {
            _step = new VulnHunterInstallStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<VulnHunterInstallStep>>();
            _item = new GalleryItem();
            _item.Tags.Add("omarchy");
            _enabled = NewOptions(true);
            _none = NewOptions(false);
        }

        private VmCustomizations NewOptions(bool enabled) => new()
        {
            DistributionOptions =
            {
                new DistributionOptionSelection { Name = _step.Name, IsEnabled = enabled }
            }
        };

        private void SetupResult(string output)
            => _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(output);

        [TestMethod]
        public void Metadata_IsCorrect()
        {
            Assert.AreEqual("Install VulnHunter (agentic AI scanner)", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(233, _step.Order);
            Assert.AreEqual("Sub_VulnHunterInstall", _step.ProgressPhaseId);
            Assert.AreEqual(_step.DeployPhaseId, _step.ProgressPhaseId);
            Assert.IsTrue(_step.IsOptional);
            Assert.IsFalse(_step.DefaultEnabled);
        }

        [TestMethod]
        public void IsApplicable_TagAndOptInGated()
        {
            Assert.IsTrue(_step.IsApplicable(_item, _enabled));
            Assert.IsFalse(_step.IsApplicable(_item, _none), "opt-in: not enabled => not applicable");
            Assert.IsFalse(_step.IsApplicable(_item, null));
            Assert.IsFalse(_step.IsApplicable(new GalleryItem(), _enabled), "no omarchy tag => not offered");
            Assert.IsTrue(_step.IsVisibleFor(_item));
        }

        [TestMethod]
        public async Task Execute_Ok_LogsInfo()
        {
            SetupResult("cloned\nVULNHUNTER_RESULT=ok");
            await _step.ExecuteAsync(_shell.Object, _item, _enabled, _logger.Object, CancellationToken.None);
            _shell.Verify(s => s.CopyContentAsync(It.Is<string>(c => c.Contains("VULNHUNTER_RESULT=")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task Execute_Skipped_LogsWarning_NotThrows()
        {
            SetupResult("SKIP: python 3.11+ not found\nVULNHUNTER_RESULT=skipped");
            await _step.ExecuteAsync(_shell.Object, _item, _enabled, _logger.Object, CancellationToken.None);
            _logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("SKIPPED")),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [TestMethod]
        public void ParseResult_HandlesContract()
        {
            Assert.AreEqual("ok", VulnHunterInstallStep.ParseResult("VULNHUNTER_RESULT=ok"));
            Assert.AreEqual("skipped", VulnHunterInstallStep.ParseResult("x\nVULNHUNTER_RESULT=skipped "));
            Assert.IsNull(VulnHunterInstallStep.ParseResult("no marker"));
            Assert.IsNull(VulnHunterInstallStep.ParseResult("VULNHUNTER_RESULT=bogus"));
        }
    }
}
