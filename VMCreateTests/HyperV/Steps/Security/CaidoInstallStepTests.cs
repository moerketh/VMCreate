using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>Tests for <see cref="CaidoInstallStep"/> — optional, omarchy-gated.</summary>
    [TestClass]
    public sealed class CaidoInstallStepTests
    {
        private CaidoInstallStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<CaidoInstallStep>> _logger = null!;
        private GalleryItem _item = null!;
        private VmCustomizations _enabled = null!;

        [TestInitialize]
        public void Setup()
        {
            _step = new CaidoInstallStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<CaidoInstallStep>>();
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
            Assert.AreEqual("Install Caido web proxy", _step.Name);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(235, _step.Order);
            Assert.AreEqual("Sub_CaidoInstall", _step.ProgressPhaseId);
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
            SetupResult("installed caido-desktop via paru\nCAIDO_RESULT=ok");
            await _step.ExecuteAsync(_shell.Object, _item, _enabled, _logger.Object, CancellationToken.None);
            _shell.Verify(s => s.CopyContentAsync(It.Is<string>(c => c.Contains("CAIDO_RESULT=")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task Execute_Skipped_LogsWarning()
        {
            SetupResult("SKIP: curl not found\nCAIDO_RESULT=skipped");
            await _step.ExecuteAsync(_shell.Object, _item, _enabled, _logger.Object, CancellationToken.None);
            _logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("SKIPPED")),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [TestMethod]
        public void ParseResult_HandlesContract()
        {
            Assert.AreEqual("ok", CaidoInstallStep.ParseResult("CAIDO_RESULT=ok"));
            Assert.AreEqual("skipped", CaidoInstallStep.ParseResult("CAIDO_RESULT=skipped"));
            Assert.IsNull(CaidoInstallStep.ParseResult("none"));
        }
    }
}
