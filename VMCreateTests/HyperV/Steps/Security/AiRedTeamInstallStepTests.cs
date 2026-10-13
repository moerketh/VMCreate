using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>Tests for <see cref="AiRedTeamInstallStep"/> — optional, omarchy-gated.</summary>
    [TestClass]
    public sealed class AiRedTeamInstallStepTests
    {
        private AiRedTeamInstallStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<AiRedTeamInstallStep>> _logger = null!;
        private GalleryItem _item = null!;
        private VmCustomizations _enabled = null!;

        [TestInitialize]
        public void Setup()
        {
            _step = new AiRedTeamInstallStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<AiRedTeamInstallStep>>();
            _item = new GalleryItem();
            _item.Tags.Add("omarchy");
            _enabled = new VmCustomizations
            {
                DistributionOptions = { new DistributionOptionSelection { Name = _step.Name, IsEnabled = true } }
            };
        }

        private void SetupResult(string output)
            => _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(output);

        [TestMethod]
        public void Metadata_IsCorrect()
        {
            Assert.AreEqual("Install AI red-team tools", _step.Name);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(234, _step.Order);
            Assert.AreEqual("Sub_AiRedTeamInstall", _step.ProgressPhaseId);
            Assert.IsTrue(_step.IsOptional);
            Assert.IsFalse(_step.DefaultEnabled);
        }

        [TestMethod]
        public void IsApplicable_OptIn()
        {
            Assert.IsTrue(_step.IsApplicable(_item, _enabled));
            Assert.IsFalse(_step.IsApplicable(_item, new VmCustomizations()));
            Assert.IsFalse(_step.IsApplicable(new GalleryItem(), _enabled));
        }

        [TestMethod]
        public async Task Execute_Ok()
        {
            SetupResult("installed: garak\nAIREdTEAM_RESULT=ok");
            await _step.ExecuteAsync(_shell.Object, _item, _enabled, _logger.Object, CancellationToken.None);
            _shell.Verify(s => s.CopyContentAsync(It.Is<string>(c => c.Contains("garak") && c.Contains("AIREdTEAM_RESULT=")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task Execute_Degraded_LogsWarning()
        {
            SetupResult("WARN: failed to install pyrit\nAIREdTEAM_RESULT=degraded");
            await _step.ExecuteAsync(_shell.Object, _item, _enabled, _logger.Object, CancellationToken.None);
            _logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("DEGRADED")),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [TestMethod]
        public void ParseResult_HandlesContract()
        {
            Assert.AreEqual("ok", AiRedTeamInstallStep.ParseResult("AIREdTEAM_RESULT=ok"));
            Assert.AreEqual("degraded", AiRedTeamInstallStep.ParseResult("AIREdTEAM_RESULT=degraded"));
            Assert.IsNull(AiRedTeamInstallStep.ParseResult("none"));
        }
    }
}
