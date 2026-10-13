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
    /// Tests for <see cref="OmarchyAppRegistrationStep"/> — required (not
    /// user-togglable), omarchy-gated, runs after all install steps.
    /// </summary>
    [TestClass]
    public sealed class OmarchyAppRegistrationStepTests
    {
        private OmarchyAppRegistrationStep _step = null!;
        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger<OmarchyAppRegistrationStep>> _logger = null!;
        private GalleryItem _item = null!;
        private GalleryItem _plain = null!;

        [TestInitialize]
        public void Setup()
        {
            _step = new OmarchyAppRegistrationStep();
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            _logger = new Mock<ILogger<OmarchyAppRegistrationStep>>();
            _item = new GalleryItem();
            _item.Tags.Add("omarchy");
            _plain = new GalleryItem();
        }

        private void SetupResult(string output)
            => _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(output);

        [TestMethod]
        public void Metadata_IsCorrect()
        {
            Assert.AreEqual("Register security apps in launcher", _step.Name);
            Assert.AreEqual(StepPlatform.Linux, _step.Platform);
            Assert.AreEqual(CustomizationPhase.PostBoot, _step.Phase);
            Assert.AreEqual(236, _step.Order);
            Assert.AreEqual("Sub_OmarchyRegisterApps", _step.ProgressPhaseId);
            Assert.AreEqual(_step.DeployPhaseId, _step.ProgressPhaseId);
        }

        [TestMethod]
        public void IsApplicable_TagGated_AndRequired()
        {
            Assert.IsTrue(_step.IsApplicable(_item, new VmCustomizations()));
            Assert.IsTrue(_step.IsApplicable(_item, null), "required — not gated on user toggle");
            Assert.IsFalse(_step.IsApplicable(_plain, new VmCustomizations()));
            Assert.IsTrue(_step.IsVisibleFor(_item));
            Assert.IsFalse(_step.IsVisibleFor(_plain));
        }

        [TestMethod]
        public async Task Execute_Ok_RegistersApps()
        {
            SetupResult("registered: CyberChef\nregistered: RevShells\nAPPS_RESULT=ok");
            await _step.ExecuteAsync(_shell.Object, _item, new VmCustomizations(), _logger.Object, CancellationToken.None);
            _shell.Verify(s => s.CopyContentAsync(
                It.Is<string>(c => c.Contains("CyberChef") && c.Contains(".local/share/applications") && c.Contains("APPS_RESULT=")),
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task Execute_Degraded_LogsWarning_NotThrows()
        {
            SetupResult("WARN: no browser found\nAPPS_RESULT=degraded");
            await _step.ExecuteAsync(_shell.Object, _item, new VmCustomizations(), _logger.Object, CancellationToken.None);
            _logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("DEGRADED")),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [TestMethod]
        public void ParseResult_HandlesContract()
        {
            Assert.AreEqual("ok", OmarchyAppRegistrationStep.ParseResult("APPS_RESULT=ok"));
            Assert.AreEqual("degraded", OmarchyAppRegistrationStep.ParseResult("x\nAPPS_RESULT=degraded"));
            Assert.IsNull(OmarchyAppRegistrationStep.ParseResult("none"));
        }
    }
}
