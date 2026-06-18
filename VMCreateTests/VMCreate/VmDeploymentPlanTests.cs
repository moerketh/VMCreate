using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using VMCreate;

namespace VMCreate.Tests.VMCreate
{
    [TestClass]
    public class VmDeploymentPlanTests
    {
        [TestMethod]
        public void Constructor_NullName_Throws()
        {
            try
            {
                _ = new VmDeploymentPlan(null);
                Assert.Fail("Expected ArgumentException for null VM name.");
            }
            catch (ArgumentException) { }
        }

        [TestMethod]
        public void Constructor_WhitespaceName_Throws()
        {
            try
            {
                _ = new VmDeploymentPlan("  ");
                Assert.Fail("Expected ArgumentException for whitespace VM name.");
            }
            catch (ArgumentException) { }
        }

        [TestMethod]
        public void Constructor_EmptyName_Throws()
        {
            try
            {
                _ = new VmDeploymentPlan(string.Empty);
                Assert.Fail("Expected ArgumentException for empty VM name.");
            }
            catch (ArgumentException) { }
        }

        [TestMethod]
        public void FromSettings_MapsAllFields()
        {
            var settings = new VmSettings
            {
                VMName = "TestVM",
                MemoryInMB = 8192,
                CPUCount = 4,
                VirtualizationEnabled = true,
                NewDriveSizeInGB = 100,
                AutoDetectDiskSize = true,
                EnhancedSessionTransportType = "HvSocket",
                SecureBoot = true,
                SecureBootTemplate = "MicrosoftWindows",
                ReplacePreviousVm = true,
                CloningIsoPath = Path.Combine(Path.GetTempPath(), "custom.iso")
            };

            var plan = VmDeploymentPlan.FromSettings(settings);

            Assert.AreEqual("TestVM", plan.VmName);
            Assert.AreEqual(8192, plan.MemoryInMB);
            Assert.AreEqual(4, plan.CpuCount);
            Assert.IsTrue(plan.VirtualizationEnabled);
            Assert.AreEqual(100, plan.NewDriveSizeInGB);
            Assert.IsTrue(plan.AutoDetectDiskSize);
            Assert.AreEqual("HvSocket", plan.EnhancedSessionTransportType);
            Assert.IsTrue(plan.SecureBoot);
            Assert.AreEqual("MicrosoftWindows", plan.SecureBootTemplate);
            Assert.IsTrue(plan.ReplacePreviousVm);
            Assert.AreEqual(settings.CloningIsoPath, plan.CloningIsoPath);
        }

        [TestMethod]
        public void FromSettings_DefaultsToBuiltInCloningIso()
        {
            var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "TestVM" });

            Assert.IsFalse(string.IsNullOrEmpty(plan.CloningIsoPath));
            StringAssert.Contains(plan.CloningIsoPath, "hyperv-convert.iso");
        }

        [TestMethod]
        public void WithVmName_ReturnsCopyWithNewName()
        {
            var original = VmDeploymentPlan.FromSettings(new VmSettings
            {
                VMName = "TestVM",
                MemoryInMB = 8192,
                NewDriveSizeInGB = 100
            });

            var stamped = original.WithVmName("TestVM_20260101120000");

            Assert.AreEqual("TestVM_20260101120000", stamped.VmName);
            Assert.AreEqual(original.MemoryInMB, stamped.MemoryInMB);
            Assert.AreEqual(original.CpuCount, stamped.CpuCount);
            Assert.AreEqual(original.NewDriveSizeInGB, stamped.NewDriveSizeInGB);
            Assert.AreEqual(original.AutoDetectDiskSize, stamped.AutoDetectDiskSize);
            Assert.AreEqual(original.CloningIsoPath, stamped.CloningIsoPath);
            Assert.AreNotSame(original, stamped);

            // Original is unchanged
            Assert.AreEqual("TestVM", original.VmName);
        }

        [TestMethod]
        public void Record_Equality_BasedOnValues()
        {
            var a = new VmDeploymentPlan("VM", memoryInMB: 4096, cpuCount: 2);
            var b = new VmDeploymentPlan("VM", memoryInMB: 4096, cpuCount: 2);
            var c = new VmDeploymentPlan("VM", memoryInMB: 2048, cpuCount: 2);

            Assert.AreEqual(a, b);
            Assert.AreNotEqual(a, c);
        }
    }
}
