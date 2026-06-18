using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;
using VMCreate.HyperV.VmCreation;

namespace VMCreate.Tests.HyperV.VmCreation
{
    [TestClass]
    public class PostBootCustomizationServiceTests
    {
        private Mock<ILogger<PostBootCustomizationService>> _loggerMock;
        private Mock<ILogger> _stepLoggerMock;
        private Mock<IGuestShell> _shellMock;
        private Mock<IProgress<CreateVMProgressInfo>> _progressMock;

        [TestInitialize]
        public void Setup()
        {
            _loggerMock = new Mock<ILogger<PostBootCustomizationService>>();
            _stepLoggerMock = new Mock<ILogger>();
            _shellMock = new Mock<IGuestShell>();
            _progressMock = new Mock<IProgress<CreateVMProgressInfo>>();
        }

        [TestMethod]
        public void HasLinuxPostBootSteps_WhenNoApplicableSteps_ReturnsFalse()
        {
            var service = new PostBootCustomizationService(Array.Empty<ICustomizationStep>(), _loggerMock.Object);
            Assert.IsFalse(service.HasLinuxPostBootSteps(new GalleryItem(), new VmCustomizations()));
        }

        [TestMethod]
        public void HasLinuxPostBootSteps_WhenApplicableStepExists_ReturnsTrue()
        {
            var step = CreateStep("Test", CustomizationPhase.PostBoot, StepPlatform.Linux, 100, applicable: true);
            var service = new PostBootCustomizationService(new[] { step }, _loggerMock.Object);
            Assert.IsTrue(service.HasLinuxPostBootSteps(new GalleryItem(), new VmCustomizations()));
        }

        [TestMethod]
        public void HasWindowsPostBootSteps_WhenApplicableStepExists_ReturnsTrue()
        {
            var step = CreateStep("WinTest", CustomizationPhase.PostBoot, StepPlatform.Windows, 100, applicable: true);
            var service = new PostBootCustomizationService(new[] { step }, _loggerMock.Object);
            Assert.IsTrue(service.HasWindowsPostBootSteps(new GalleryItem(), new VmCustomizations()));
        }

        [TestMethod]
        public async Task RunLinuxPostBootAsync_ExecutesApplicableStepsInOrder_AndReportsProgress()
        {
            var executed = new List<string>();

            var stepA = CreateExecutableStep("StepA", CustomizationPhase.PostBoot, StepPlatform.Linux, 100,
                onExecute: () => executed.Add("StepA"));
            var stepB = CreateExecutableStep("StepB", CustomizationPhase.PostBoot, StepPlatform.Linux, 200,
                onExecute: () => executed.Add("StepB"));
            var preBootStep = CreateExecutableStep("PreBoot", CustomizationPhase.PreBoot, StepPlatform.Linux, 50,
                onExecute: () => executed.Add("PreBoot"));
            var windowsStep = CreateExecutableStep("WinStep", CustomizationPhase.PostBoot, StepPlatform.Windows, 150,
                onExecute: () => executed.Add("WinStep"));

            var service = new PostBootCustomizationService(
                new[] { stepB, stepA, preBootStep, windowsStep },
                _loggerMock.Object);

            await service.RunLinuxPostBootAsync(
                _shellMock.Object,
                VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM" }),
                new GalleryItem(),
                new VmCustomizations(),
                _progressMock.Object,
                CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "StepA", "StepB" }, executed);

            _progressMock.Verify(p => p.Report(It.Is<CreateVMProgressInfo>(
                r => r.Phase == VmDeploymentPhase.PostBoot && r.ProgressPercentage == 0 && r.StepName == "StepA")),
                Times.Once);

            _progressMock.Verify(p => p.Report(It.Is<CreateVMProgressInfo>(
                r => r.Phase == VmDeploymentPhase.PostBoot && r.ProgressPercentage == 100)),
                Times.Once);
        }

        [TestMethod]
        public async Task RunWindowsPostBootAsync_WaitsForShellReadyAfterEachStep()
        {
            var executed = new List<string>();

            var step = CreateExecutableStep("WinStep", CustomizationPhase.PostBoot, StepPlatform.Windows, 100,
                onExecute: () => executed.Add("WinStep"));

            var service = new PostBootCustomizationService(new[] { step }, _loggerMock.Object);

            await service.RunWindowsPostBootAsync(
                _shellMock.Object,
                VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM" }),
                new GalleryItem(),
                new VmCustomizations(),
                _progressMock.Object,
                CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "WinStep" }, executed);
            _shellMock.Verify(s => s.WaitForReadyAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task RunLinuxPostBootAsync_NoApplicableSteps_ReportsNoProgress()
        {
            var service = new PostBootCustomizationService(Array.Empty<ICustomizationStep>(), _loggerMock.Object);

            await service.RunLinuxPostBootAsync(
                _shellMock.Object,
                VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM" }),
                new GalleryItem(),
                new VmCustomizations(),
                _progressMock.Object,
                CancellationToken.None);

            _progressMock.Verify(p => p.Report(It.IsAny<CreateVMProgressInfo>()), Times.Never);
        }

        private static ICustomizationStep CreateStep(
            string name,
            CustomizationPhase phase,
            StepPlatform platform,
            int order,
            bool applicable)
        {
            var mock = new Mock<ICustomizationStep>();
            mock.Setup(s => s.Name).Returns(name);
            mock.Setup(s => s.Phase).Returns(phase);
            mock.Setup(s => s.Platform).Returns(platform);
            mock.Setup(s => s.Order).Returns(order);
            mock.Setup(s => s.IsApplicable(It.IsAny<GalleryItem>(), It.IsAny<VmCustomizations>()))
                .Returns(applicable);
            return mock.Object;
        }

        private static ICustomizationStep CreateExecutableStep(
            string name,
            CustomizationPhase phase,
            StepPlatform platform,
            int order,
            Action onExecute)
        {
            var mock = new Mock<ICustomizationStep>();
            mock.Setup(s => s.Name).Returns(name);
            mock.Setup(s => s.Phase).Returns(phase);
            mock.Setup(s => s.Platform).Returns(platform);
            mock.Setup(s => s.Order).Returns(order);
            mock.Setup(s => s.ProgressPhaseId).Returns((string)null);
            mock.Setup(s => s.IsApplicable(It.IsAny<GalleryItem>(), It.IsAny<VmCustomizations>()))
                .Returns(true);
            mock.Setup(s => s.ExecuteAsync(
                    It.IsAny<IGuestShell>(),
                    It.IsAny<GalleryItem>(),
                    It.IsAny<VmCustomizations>(),
                    It.IsAny<ILogger>(),
                    It.IsAny<CancellationToken>()))
                .Callback(onExecute)
                .Returns(Task.CompletedTask);
            return mock.Object;
        }
    }
}
