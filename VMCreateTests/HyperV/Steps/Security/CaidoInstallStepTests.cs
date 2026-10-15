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
        public async Task Execute_ShellCommandRunsScriptUnderBash_CleansUpWithSudoRm()
        {
            // Regression (2026-10-15 deployment abort): CopyContentAsync lands
            // scripts as root:root 0644. An unprivileged "chmod 0755" EPERMs
            // (SSH exit 1), and a bare "rm -f" of the root-owned file EPERMs
            // too — bash needs only world-readable and cleanup needs sudo.
            string? guestPath = null;
            _shell.Setup(s => s.CopyContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((_, p, _) => guestPath = p)
                  .Returns(Task.CompletedTask);
            string? command = null;
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .Callback<string, TimeSpan, CancellationToken>((c, _, _) => command = c)
                  .ReturnsAsync("CAIDO_RESULT=ok");

            await _step.ExecuteAsync(_shell.Object, _item, _enabled, _logger.Object, CancellationToken.None);

            Assert.IsNotNull(guestPath);
            Assert.AreEqual($"bash {guestPath}; rc=$?; sudo rm -f {guestPath}; exit $rc", command);
            Assert.IsFalse(command!.Contains("chmod"), command);
        }

        [TestMethod]
        public async Task Execute_CommandFailure_LogsWarning_NotThrows()
        {
            // SshTransport throws a plain Exception on non-zero SSH exit; per
            // this optional step's contract it never fails the deployment.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new Exception("SSH command failed (exit code 1) on VM 'TestVM': Operation not permitted"));

            await _step.ExecuteAsync(_shell.Object, _item, _enabled, _logger.Object, CancellationToken.None);

            _logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => string.Concat(v).Contains("deployment continues")),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [TestMethod]
        public async Task Execute_CancellationStillThrows()
        {
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new OperationCanceledException());

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => _step.ExecuteAsync(_shell.Object, _item, _enabled, _logger.Object, CancellationToken.None));
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
