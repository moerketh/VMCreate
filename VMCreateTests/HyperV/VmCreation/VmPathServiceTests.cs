using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.IO;
using VMCreate.HyperV.VmCreation;

namespace VMCreate.Tests.HyperV.VmCreation
{
    [TestClass]
    public sealed class VmPathServiceTests
    {
        [TestMethod]
        public void GetVirtualHardDiskPath_ReturnsSubdirectoryUnderDefaultVhdxPath()
        {
            var service = new VmPathService(Mock.Of<ILogger<VmPathService>>());

            string path = service.GetVirtualHardDiskPath("TestVM");

            Assert.IsTrue(path.EndsWith(@"\TestVM"), $"Expected subdirectory, got: {path}");
            Assert.IsTrue(path.StartsWith(service.DefaultVhdxPath), "Expected path under DefaultVhdxPath");
        }

        [TestMethod]
        public void DefaultPaths_AreNotNull()
        {
            var service = new VmPathService(Mock.Of<ILogger<VmPathService>>());

            Assert.IsFalse(string.IsNullOrWhiteSpace(service.DefaultVmPath));
            Assert.IsFalse(string.IsNullOrWhiteSpace(service.DefaultVhdxPath));
        }

        [TestMethod]
        public void DefaultVmPath_ContainsHyperVDirectoryOrFallback()
        {
            var service = new VmPathService(Mock.Of<ILogger<VmPathService>>());

            bool isHyperVPath = service.DefaultVmPath.Contains("Hyper-V") || service.DefaultVmPath.Contains("Virtualization");
            Assert.IsTrue(isHyperVPath, $"Expected a Hyper-V related path, got: {service.DefaultVmPath}");
        }

        [TestMethod]
        public void DefaultVhdxPath_ContainsVirtualHardDisksOrFallback()
        {
            var service = new VmPathService(Mock.Of<ILogger<VmPathService>>());

            bool isVhdxPath = service.DefaultVhdxPath.Contains("Virtual Hard Disks") || service.DefaultVhdxPath.Contains("Hyper-V");
            Assert.IsTrue(isVhdxPath, $"Expected a Virtual Hard Disks path, got: {service.DefaultVhdxPath}");
        }
    }
}
