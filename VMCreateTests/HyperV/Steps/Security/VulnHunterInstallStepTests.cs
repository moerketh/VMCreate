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
                  .ReturnsAsync("VULNHUNTER_RESULT=ok");

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
            Assert.AreEqual("ok", VulnHunterInstallStep.ParseResult("VULNHUNTER_RESULT=ok"));
            Assert.AreEqual("skipped", VulnHunterInstallStep.ParseResult("x\nVULNHUNTER_RESULT=skipped "));
            Assert.IsNull(VulnHunterInstallStep.ParseResult("no marker"));
            Assert.IsNull(VulnHunterInstallStep.ParseResult("VULNHUNTER_RESULT=bogus"));
        }
    }
}
