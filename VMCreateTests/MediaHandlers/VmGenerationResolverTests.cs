using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.MediaHandlers;

namespace VMCreate.Tests.MediaHandlers
{
    [TestClass]
    public class VmGenerationResolverTests
    {
        private Mock<IPartitionSchemeDetector> _partitionDetectorMock;
        private Mock<IDiskConverter> _diskConverterMock;
        private Mock<ILogger<VmGenerationResolver>> _loggerMock;

        [TestInitialize]
        public void Setup()
        {
            _partitionDetectorMock = new Mock<IPartitionSchemeDetector>();
            _diskConverterMock = new Mock<IDiskConverter>();
            _loggerMock = new Mock<ILogger<VmGenerationResolver>>();
        }

        [TestMethod]
        public async Task ResolveAsync_GptPartitionScheme_ReturnsGeneration2WithoutSizeCheck()
        {
            _partitionDetectorMock.Setup(d => d.DetectPartitionSchemeAsync(It.IsAny<string>()))
                .ReturnsAsync("GPT");

            var resolver = new VmGenerationResolver(
                _partitionDetectorMock.Object,
                _diskConverterMock.Object,
                _loggerMock.Object);

            var result = await resolver.ResolveAsync(
                "dummy.vhdx",
                VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM" }),
                CancellationToken.None);

            Assert.AreEqual(2, result.VmGeneration);
            Assert.AreEqual("GPT", result.PartitionScheme);
            Assert.AreEqual(0, result.DetectedVirtualSizeBytes);
            _diskConverterMock.Verify(d => d.GetVirtualSizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ResolveAsync_MbrPartitionScheme_ReturnsGeneration1AndAutoSize()
        {
            const long fiveGB = 5L * 1024 * 1024 * 1024;
            _partitionDetectorMock.Setup(d => d.DetectPartitionSchemeAsync(It.IsAny<string>()))
                .ReturnsAsync("MBR");
            _diskConverterMock.Setup(d => d.GetVirtualSizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(fiveGB);

            var vmSettings = new VmSettings { VMName = "TestVM", AutoDetectDiskSize = true };
            var plan = VmDeploymentPlan.FromSettings(vmSettings);
            var resolver = new VmGenerationResolver(
                _partitionDetectorMock.Object,
                _diskConverterMock.Object,
                _loggerMock.Object);

            var result = await resolver.ResolveAsync("dummy.vhdx", plan, CancellationToken.None);

            Assert.AreEqual(1, result.VmGeneration);
            Assert.AreEqual("MBR", result.PartitionScheme);
            Assert.AreEqual(fiveGB, result.DetectedVirtualSizeBytes);
            Assert.AreEqual(7, result.NewDriveSizeInGB); // max(110% of 5GB, 5GB+2GB) rounded up
        }

        [TestMethod]
        public async Task ResolveAsync_MbrWithManualSizeTooSmall_ThrowsInvalidOperationException()
        {
            const long tenGB = 10L * 1024 * 1024 * 1024;
            _partitionDetectorMock.Setup(d => d.DetectPartitionSchemeAsync(It.IsAny<string>()))
                .ReturnsAsync("MBR");
            _diskConverterMock.Setup(d => d.GetVirtualSizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(tenGB);

            var vmSettings = new VmSettings { VMName = "TestVM", AutoDetectDiskSize = false, NewDriveSizeInGB = 5 };
            var plan = VmDeploymentPlan.FromSettings(vmSettings);
            var resolver = new VmGenerationResolver(
                _partitionDetectorMock.Object,
                _diskConverterMock.Object,
                _loggerMock.Object);

            try
            {
                await resolver.ResolveAsync("dummy.vhdx", plan, CancellationToken.None);
                Assert.Fail("Expected InvalidOperationException for undersized drive.");
            }
            catch (InvalidOperationException ex)
            {
                StringAssert.Contains(ex.Message, "too small for the source disk");
            }
        }

        [TestMethod]
        public async Task ResolveAsync_MbrWithManualSizeLargeEnough_DoesNotThrow()
        {
            const long fiveGB = 5L * 1024 * 1024 * 1024;
            _partitionDetectorMock.Setup(d => d.DetectPartitionSchemeAsync(It.IsAny<string>()))
                .ReturnsAsync("MBR");
            _diskConverterMock.Setup(d => d.GetVirtualSizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(fiveGB);

            var vmSettings = new VmSettings { VMName = "TestVM", AutoDetectDiskSize = false, NewDriveSizeInGB = 20 };
            var plan = VmDeploymentPlan.FromSettings(vmSettings);
            var resolver = new VmGenerationResolver(
                _partitionDetectorMock.Object,
                _diskConverterMock.Object,
                _loggerMock.Object);

            var result = await resolver.ResolveAsync("dummy.vhdx", plan, CancellationToken.None);

            Assert.AreEqual(1, result.VmGeneration);
            Assert.AreEqual(20, plan.NewDriveSizeInGB);
        }
    }
}